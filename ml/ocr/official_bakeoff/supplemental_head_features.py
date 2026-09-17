# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Cache reviewed frozen-trunk features for the six supplemental train panels.

This helper is deliberately separate from ``captured_head_features``. It
authenticates the fixed supplemental composition, capture request, capture
source, runtime assemblies, and tensor bytes before loading the reviewed model.
It performs step-zero parity only and never creates an optimizer.
"""
from __future__ import annotations

from hashlib import sha256
import json
import math
from pathlib import Path
import time
from typing import Any, Mapping, Sequence

import numpy as np

from ml.ocr.official_bakeoff import frozen_trunk_head as head_api


TOLERANCE = 1e-5
SCHEMA = "graphreader.supplemental-official-head-features.v1"
COMPOSITION_PATH = Path(
    "artifacts/goal22-runs/ocr-supplemental-head-capture/composition-verification.json"
)
COMPOSITION_SHA256 = "f0591ae25244b570ea2077d34b40dac96b40be1433d6ca89a0119989378529c0"
CAPTURE_PATH = Path(
    "artifacts/goal22-runs/ocr-supplemental-head-capture/tensors/report.json"
)
CAPTURE_SHA256 = "2c605fb18cb0195453f1b569506546c22c3a873db19475e1fb87daa0bddaeb19"
REQUEST_PATH = Path("artifacts/goal22-runs/ocr-train-legend-head-inputs-v1/request.json")
REQUEST_SHA256 = "0e795a4d17b1b1d0b2f652a30ffff30e038ec51eae61a9699510d65b43eb6cf1"
CAPTURE_SOURCE_PATH = Path(
    "tools/GraphReader.SyntheticRuntimeEvidence/OfficialHeadTensorCapture.cs"
)
CAPTURE_SOURCE_SHA256 = "5b388c8064ef06b22352c8df72afb6b63df7c2f2ae00812015e83975f848823d"
HISTORICAL_CAPTURE_SHA256 = "15e48b27ce3b8f5f53311abbef7235c35b3e8e361c8baddb09a4d3a7e7d5938e"
FEATURE_MODEL_SHA256 = "6115e9e02bb20ea495b04e3f5329721115ba72b5e79cc7246773919c9699b081"
FROZEN_HEAD_SOURCE_SHA256 = "6171df9298f785360695353087344229a8f761cfda51bb3aa728ddab10997703"
ASSEMBLIES: tuple[tuple[str, Path, str], ...] = (
    (
        "GraphReader.SyntheticRuntimeEvidence",
        Path("artifacts/goal22-runs/ocr-supplemental-head-capture/runtime/GraphReader.SyntheticRuntimeEvidence.dll"),
        "22257da1e591ba2554ccc8e01ac3a11316dbff9532f2e5e204c69d0752863b66",
    ),
    (
        "GraphReader.App",
        Path("artifacts/goal22-runs/ocr-supplemental-head-capture/runtime/GraphReader.App.dll"),
        "3993313a878538a05014fe3d4194ce828bcb2cdaadbc386f90cea9962c19156c",
    ),
    (
        "GraphReader.Ocr",
        Path("artifacts/goal22-runs/ocr-supplemental-head-capture/runtime/GraphReader.Ocr.dll"),
        "fc0893a8c913ef5777d386d44b90d46034e27a9f3bc7081400dd7fb49bdf7186",
    ),
    (
        "GraphReader.Inference",
        Path("artifacts/goal22-runs/ocr-supplemental-head-capture/runtime/GraphReader.Inference.dll"),
        "a0c7d32dc415f561c8351b16a631bf8b8e990c93763fec1c014a03c0871675fc",
    ),
)


class SupplementalFeatureError(ValueError):
    """A trusted composition, capture, feature, or parity invariant failed."""


def _digest(path: Path) -> str:
    return sha256(path.read_bytes()).hexdigest()


def _pairs(items: list[tuple[str, Any]]) -> dict[str, Any]:
    result: dict[str, Any] = {}
    for key, value in items:
        if key in result:
            raise SupplementalFeatureError("Duplicate JSON field")
        result[key] = value
    return result


def _bound(path: Path, expected: str) -> bytes:
    if (
        not isinstance(expected, str)
        or len(expected) != 64
        or any(character not in "0123456789abcdef" for character in expected)
    ):
        raise SupplementalFeatureError("Invalid SHA-256")
    payload = path.read_bytes()
    if sha256(payload).hexdigest() != expected:
        raise SupplementalFeatureError("Bound input bytes changed")
    return payload


def _inside(root: Path, path: Path) -> Path:
    if path.is_absolute():
        raise SupplementalFeatureError("Artifact path must be relative")
    result = (root / path).resolve()
    if not result.is_relative_to(root):
        raise SupplementalFeatureError("Artifact path escapes repository root")
    return result


def _load_json(path: Path, expected_sha256: str) -> dict[str, Any]:
    try:
        value = json.loads(_bound(path, expected_sha256), object_pairs_hook=_pairs)
    except (json.JSONDecodeError, UnicodeDecodeError) as error:
        raise SupplementalFeatureError("Bound JSON is invalid") from error
    if not isinstance(value, dict):
        raise SupplementalFeatureError("Bound JSON root must be an object")
    return value


def _same_value(mapping: Mapping[str, Any], expected: Mapping[str, Any], label: str) -> None:
    for key, value in expected.items():
        observed = mapping.get(key)
        if type(observed) is not type(value) or observed != value:
            raise SupplementalFeatureError(f"{label} changed: {key}")


def _mapping(value: Any, label: str) -> Mapping[str, Any]:
    if not isinstance(value, dict):
        raise SupplementalFeatureError(f"{label} must be an object")
    return value


def _binding(rows: Any, name: str, path: Path, digest: str) -> None:
    if not isinstance(rows, list):
        raise SupplementalFeatureError("Composition artifact bindings are missing")
    matches = [row for row in rows if isinstance(row, dict) and row.get("name") == name]
    if matches != [{"name": name, "path": path.as_posix(), "sha256": digest}]:
        raise SupplementalFeatureError(f"Composition binding changed: {name}")


def _identity_rows(rows: Any, expected: Sequence[tuple[str, Path, str]], label: str) -> None:
    required = [
        {"name": name, "path": path.as_posix(), "sha256": digest}
        for name, path, digest in expected
    ]
    if rows != required:
        raise SupplementalFeatureError(f"{label} identity changed")


def _validate_composition(report: Mapping[str, Any]) -> None:
    _same_value(
        report,
        {
            "schema": "graphreader.supplemental-legend-head-input-verification.v1",
            "scope": "project-owned-synthetic-train-only-supplemental-model-free",
            "status": "complete_authenticated_composition",
            "acceptance_gate_changed": False,
            "historical_train_objects_unchanged": True,
            "supplemental_request_exactly_reconstructed": True,
            "model_inference_runs": 0,
            "optimizer_steps": 0,
            "private_reads": 0,
            "sealed_reads": 0,
            "production_approved": False,
            "training_authorized": False,
        },
        "Trusted composition",
    )
    _same_value(
        _mapping(report.get("historical_train"), "Historical train inventory"),
        {"panel_count": 28, "source_count": 20, "truth_count": 709},
        "Historical train inventory",
    )
    _same_value(
        _mapping(report.get("fixed_development"), "Fixed development inventory"),
        {
            "panel_count": 9,
            "same_object": True,
            "source_count": 3,
            "tensor_hashes_unchanged": True,
            "truth_count": 183,
        },
        "Fixed development inventory",
    )
    _same_value(
        _mapping(report.get("combined_train"), "Combined train inventory"),
        {"panel_count": 34, "source_count": 26, "truth_count": 862},
        "Combined train inventory",
    )
    _same_value(
        _mapping(report.get("supplemental_train"), "Supplemental train inventory"),
        {"panel_count": 6, "source_count": 6, "truth_count": 153},
        "Supplemental train inventory",
    )
    bindings = report.get("artifact_bindings")
    _binding(bindings, "historical_tensor_report", Path("artifacts/goal22-runs/ocr-text-extent-tensors/report.json"), HISTORICAL_CAPTURE_SHA256)
    _binding(bindings, "supplemental_request", REQUEST_PATH, REQUEST_SHA256)
    _binding(bindings, "supplemental_capture_report", CAPTURE_PATH, CAPTURE_SHA256)
    source = report.get("supplemental_capture_source")
    if source != {"path": CAPTURE_SOURCE_PATH.as_posix(), "sha256": CAPTURE_SOURCE_SHA256}:
        raise SupplementalFeatureError("Supplemental capture source identity changed")
    _identity_rows(report.get("supplemental_capture_assemblies"), ASSEMBLIES, "Supplemental assembly")


def _validate_capture_identity(report: Mapping[str, Any]) -> None:
    _same_value(
        report,
        {
            "schema": "graphreader.supplemental-official-head-tensor-capture-report.v1",
            "scope": "project-owned-synthetic-train-only-supplemental-model-free",
            "synthetic_only": True,
            "private_data": False,
            "sealed_data": False,
            "truth_used_by_capture": False,
            "model_inference": False,
            "training_input_ready": False,
            "production_approved": False,
            "detector_model_sha256": head_api.REVIEWED_DETECTOR_SHA256,
            "capture_source_sha256": CAPTURE_SOURCE_SHA256,
            "maximum_side_length": 960,
            "dimension_multiple": 128,
            "panel_count": 6,
            "failed_panel_count": 0,
        },
        "Supplemental capture",
    )
    if report.get("request") != {"path": REQUEST_PATH.as_posix(), "sha256": REQUEST_SHA256}:
        raise SupplementalFeatureError("Supplemental request identity changed")
    _identity_rows(report.get("assemblies"), ASSEMBLIES, "Capture assembly")


def _load_capture_tensors(
    *, capture_path: Path, expected_sha256: str,
) -> tuple[dict[str, Any], tuple[tuple[dict[str, Any], np.ndarray], ...]]:
    """Validate a complete six-train/zero-development supplemental capture."""
    report = _load_json(capture_path, expected_sha256)
    _validate_capture_identity(report)
    rows = report.get("panels")
    if not isinstance(rows, list) or len(rows) != 6:
        raise SupplementalFeatureError("Capture must include exactly six panels")
    root = capture_path.resolve().parent
    seen: set[str] = set()
    tensors: list[tuple[dict[str, Any], np.ndarray]] = []
    for row in rows:
        if not isinstance(row, dict) or row.get("split") != "train":
            raise SupplementalFeatureError("Supplemental capture must be train-only")
        panel_id = row.get("panel_id")
        if (
            not isinstance(panel_id, str)
            or not panel_id
            or panel_id in seen
            or Path(panel_id).name != panel_id
            or any(character in panel_id for character in "/\\:")
        ):
            raise SupplementalFeatureError("Capture panel identity is unsafe or repeated")
        seen.add(panel_id)
        tensor = row.get("tensor")
        if not isinstance(tensor, dict):
            raise SupplementalFeatureError("Capture tensor is missing")
        shape = tensor.get("shape")
        if (
            not isinstance(shape, list)
            or len(shape) != 4
            or any(type(value) is not int for value in shape)
            or shape[:2] != [1, 3]
            or any(value < 128 or value > 1024 or value % 128 for value in shape[2:])
        ):
            raise SupplementalFeatureError("Capture tensor must be bounded NCHW float32 input")
        filename = tensor.get("file")
        if not isinstance(filename, str) or not filename:
            raise SupplementalFeatureError("Capture tensor path is missing")
        path = _inside(root, Path(filename))
        raw = _bound(path, tensor.get("sha256"))
        if (
            type(tensor.get("byte_count")) is not int
            or tensor["byte_count"] != len(raw)
            or len(raw) != math.prod(shape) * 4
            or tensor.get("dtype") != "float32-le"
            or tensor.get("input_name") != head_api.MODEL_INPUT_NAME
            or tensor.get("output_name") != head_api.MODEL_OUTPUT_NAME
        ):
            raise SupplementalFeatureError("Capture tensor contract changed")
        values = np.frombuffer(raw, dtype="<f4").reshape(shape).copy()
        if not np.isfinite(values).all():
            raise SupplementalFeatureError("Capture tensor contains non-finite values")
        tensors.append((row, values))
    return report, tuple(tensors)


def authenticate_inputs(
    *, repository_root: Path, composition_report: Path = COMPOSITION_PATH,
) -> tuple[dict[str, Any], dict[str, Any], tuple[tuple[dict[str, Any], np.ndarray], ...]]:
    """Authenticate every supplemental input without loading the model."""
    root = repository_root.resolve()
    if composition_report != COMPOSITION_PATH:
        raise SupplementalFeatureError("Trusted composition path changed")
    composition_path = _inside(root, composition_report)
    composition = _load_json(composition_path, COMPOSITION_SHA256)
    _validate_composition(composition)
    _bound(_inside(root, REQUEST_PATH), REQUEST_SHA256)
    _bound(_inside(root, CAPTURE_SOURCE_PATH), CAPTURE_SOURCE_SHA256)
    for _, path, digest in ASSEMBLIES:
        _bound(_inside(root, path), digest)
    capture, tensors = _load_capture_tensors(
        capture_path=_inside(root, CAPTURE_PATH), expected_sha256=CAPTURE_SHA256
    )
    _bound(Path(head_api.__file__).resolve(), FROZEN_HEAD_SOURCE_SHA256)
    return composition, capture, tensors


def _validate_feature_report(report: Mapping[str, Any]) -> None:
    _same_value(
        report,
        {
            "schema": SCHEMA,
            "scope": "project-owned-synthetic-train-only-supplemental-model-free",
            "composition_report_sha256": COMPOSITION_SHA256,
            "capture_report_sha256": CAPTURE_SHA256,
            "capture_request_sha256": REQUEST_SHA256,
            "capture_source_sha256": CAPTURE_SOURCE_SHA256,
            "historical_capture_sha256": HISTORICAL_CAPTURE_SHA256,
            "capture_assemblies": [
                {"name": name, "path": path.as_posix(), "sha256": digest}
                for name, path, digest in ASSEMBLIES
            ],
            "source_model_sha256": head_api.REVIEWED_DETECTOR_SHA256,
            "source_code_sha256": FROZEN_HEAD_SOURCE_SHA256,
            "feature_model_sha256": FEATURE_MODEL_SHA256,
            "no_op_model_sha256": head_api.REVIEWED_DETECTOR_SHA256,
            "provider": "CPUExecutionProvider",
            "torch_head_arithmetic": "float64",
            "cpu_threads": 1,
            "torch_mkldnn_enabled": False,
            "graph_optimization": "ORT_ENABLE_ALL",
            "panel_count": 6,
            "train_panel_count": 6,
            "development_panel_count": 0,
            "tolerance": TOLERANCE,
            "passed": True,
            "optimizer_steps": 0,
            "private_reads": 0,
            "sealed_reads": 0,
            "production_approved": False,
        },
        "Supplemental feature report",
    )
    maximum = report.get("maximum_absolute_error")
    if (
        type(maximum) is not float
        or not math.isfinite(maximum)
        or maximum < 0.0
        or maximum > TOLERANCE
    ):
        raise SupplementalFeatureError("Supplemental feature parity is invalid")
    for key in ("helper_sha256",):
        value = report.get(key)
        if (
            not isinstance(value, str)
            or len(value) != 64
            or any(character not in "0123456789abcdef" for character in value)
        ):
            raise SupplementalFeatureError(f"Supplemental feature identity changed: {key}")


def _validate_cached_inventory(
    report: Mapping[str, Any], capture: Mapping[str, Any],
) -> tuple[dict[str, Any], ...]:
    rows = report.get("panels")
    capture_rows = capture.get("panels")
    if not isinstance(rows, list) or len(rows) != 6:
        raise SupplementalFeatureError("Feature cache must include exactly six panels")
    if not isinstance(capture_rows, list) or len(capture_rows) != 6:
        raise SupplementalFeatureError("Authenticated capture inventory changed")
    captured: dict[str, tuple[str, list[int]]] = {}
    for capture_row in capture_rows:
        if not isinstance(capture_row, dict) or not isinstance(capture_row.get("tensor"), dict):
            raise SupplementalFeatureError("Authenticated capture inventory changed")
        captured[capture_row.get("panel_id")] = (
            capture_row["tensor"].get("sha256"), capture_row["tensor"].get("shape")
        )
    if len(captured) != 6 or any(not isinstance(key, str) for key in captured):
        raise SupplementalFeatureError("Authenticated capture panel identities changed")
    seen: set[str] = set()
    errors: list[float] = []
    validated: list[dict[str, Any]] = []
    for row in rows:
        if not isinstance(row, dict) or row.get("split") != "train":
            raise SupplementalFeatureError("Feature cache must be train-only")
        panel_id = row.get("panel_id")
        if (
            not isinstance(panel_id, str)
            or not panel_id
            or panel_id in seen
            or Path(panel_id).name != panel_id
            or any(character in panel_id for character in "/\\:")
        ):
            raise SupplementalFeatureError("Feature panel identity is unsafe or repeated")
        seen.add(panel_id)
        input_shape = row.get("input_shape")
        feature_shape = row.get("feature_shape")
        if (
            not isinstance(input_shape, list)
            or len(input_shape) != 4
            or any(type(value) is not int for value in input_shape)
            or input_shape[:2] != [1, 3]
            or any(value < 128 or value > 1024 or value % 128 for value in input_shape[2:])
            or feature_shape
            != [1, head_api.FEATURE_CHANNELS, input_shape[2] // 4, input_shape[3] // 4]
        ):
            raise SupplementalFeatureError("Cached feature shape changed")
        capture_identity = captured.get(panel_id)
        if capture_identity != (row.get("input_sha256"), input_shape):
            raise SupplementalFeatureError("Cached feature does not match authenticated capture")
        filename = row.get("feature_file")
        if filename != panel_id + ".features.f32":
            raise SupplementalFeatureError("Cached feature path changed")
        feature_sha256 = row.get("feature_sha256")
        if (
            not isinstance(feature_sha256, str)
            or len(feature_sha256) != 64
            or any(character not in "0123456789abcdef" for character in feature_sha256)
            or type(row.get("feature_byte_count")) is not int
            or row.get("feature_dtype") != "float32-le"
        ):
            raise SupplementalFeatureError("Cached feature tensor contract changed")
        error = row.get("maximum_absolute_error")
        if (
            type(error) is not float
            or not math.isfinite(error)
            or error < 0.0
            or error > TOLERANCE
        ):
            raise SupplementalFeatureError("Cached feature parity is invalid")
        errors.append(error)
        validated.append(row)
    if seen != set(captured):
        raise SupplementalFeatureError("Feature cache is not a bijection with the capture")
    if report.get("maximum_absolute_error") != max(errors):
        raise SupplementalFeatureError("Global parity maximum does not match feature rows")
    return tuple(validated)


def load_cached_features(
    *, repository_root: Path, feature_report: Path, expected_report_sha256: str,
) -> tuple[dict[str, Any], tuple[tuple[dict[str, Any], np.ndarray], ...]]:
    """Load a caller-hash-bound six-panel feature cache without model inference."""
    root = repository_root.resolve()
    report_path = _inside(root, feature_report)
    report = _load_json(report_path, expected_report_sha256)
    _validate_feature_report(report)
    _, capture, _ = authenticate_inputs(repository_root=root)
    rows = _validate_cached_inventory(report, capture)
    feature_root = report_path.parent
    _bound(feature_root / "frozen-trunk.onnx", FEATURE_MODEL_SHA256)
    _bound(feature_root / "no-op-detector.onnx", head_api.REVIEWED_DETECTOR_SHA256)
    if _digest(Path(__file__).resolve()) != report["helper_sha256"]:
        raise SupplementalFeatureError("Supplemental feature helper identity changed")
    features: list[tuple[dict[str, Any], np.ndarray]] = []
    for row in rows:
        feature_shape = row.get("feature_shape")
        path = _inside(feature_root, Path(row["feature_file"]))
        raw = _bound(path, row.get("feature_sha256"))
        if (
            row["feature_byte_count"] != len(raw)
            or len(raw) != math.prod(feature_shape) * 4
        ):
            raise SupplementalFeatureError("Cached feature tensor contract changed")
        values = np.frombuffer(raw, dtype="<f4").reshape(feature_shape).copy()
        if not np.isfinite(values).all():
            raise SupplementalFeatureError("Cached feature tensor contains non-finite values")
        features.append((row, values))
    return report, tuple(features)


def capture_features(
    *, repository_root: Path, parent_model: Path, output_directory: Path,
    composition_report: Path = COMPOSITION_PATH,
) -> dict[str, Any]:
    """Write supplemental frozen features and a step-zero parity report."""
    root = repository_root.resolve()
    output = _inside(root, output_directory)
    if not output.is_relative_to(root / "artifacts") or output.exists():
        raise SupplementalFeatureError("Use a new artifacts output directory")

    composition, _, tensors = authenticate_inputs(
        repository_root=root, composition_report=composition_report
    )
    parent = _inside(root, parent_model)
    _bound(parent, head_api.REVIEWED_DETECTOR_SHA256)

    import onnxruntime as ort
    import torch

    helper_path = Path(__file__).resolve()
    source_path = Path(head_api.__file__).resolve()
    helper_sha256 = _digest(helper_path)
    source_sha256 = _digest(source_path)
    if source_sha256 != FROZEN_HEAD_SOURCE_SHA256:
        raise SupplementalFeatureError("Frozen-head source identity changed")
    output.mkdir(parents=True, exist_ok=False)
    started = time.perf_counter()
    previous = (
        torch.get_num_threads(),
        torch.backends.mkldnn.enabled,
        torch.are_deterministic_algorithms_enabled(),
        torch.is_deterministic_algorithms_warn_only_enabled(),
    )
    try:
        torch.set_num_threads(1)
        torch.backends.mkldnn.enabled = False
        torch.use_deterministic_algorithms(True)
        bundle = head_api.extract_frozen_trunk_head(parent)
        feature_model = output / "frozen-trunk.onnx"
        feature_model_sha256 = head_api.write_frozen_trunk_model(parent, feature_model)
        if feature_model_sha256 != FEATURE_MODEL_SHA256:
            raise SupplementalFeatureError("Frozen feature model identity changed")
        options = ort.SessionOptions()
        options.intra_op_num_threads = options.inter_op_num_threads = 1
        options.graph_optimization_level = ort.GraphOptimizationLevel.ORT_ENABLE_ALL
        full = ort.InferenceSession(str(parent), options, providers=["CPUExecutionProvider"])
        trunk = ort.InferenceSession(str(feature_model), options, providers=["CPUExecutionProvider"])
        bundle.head.eval()
        rows: list[dict[str, Any]] = []
        with torch.inference_mode():
            for panel, values in tensors:
                expected = full.run(
                    [head_api.MODEL_OUTPUT_NAME], {head_api.MODEL_INPUT_NAME: values}
                )[0]
                features = trunk.run(
                    [head_api.FEATURE_OUTPUT_NAME], {head_api.MODEL_INPUT_NAME: values}
                )[0]
                required = (
                    1,
                    head_api.FEATURE_CHANNELS,
                    values.shape[2] // 4,
                    values.shape[3] // 4,
                )
                if features.shape != required or not np.isfinite(features).all():
                    raise SupplementalFeatureError("Frozen feature shape or values changed")
                # FrozenDbHead performs the reviewed head arithmetic in float64.
                actual = bundle.head(torch.from_numpy(np.ascontiguousarray(features))).numpy()
                if (
                    expected.shape != (1, 1, values.shape[2], values.shape[3])
                    or actual.shape != expected.shape
                    or not np.isfinite(expected).all()
                    or not np.isfinite(actual).all()
                ):
                    raise SupplementalFeatureError("Full detector/head outputs are incompatible")
                raw = np.ascontiguousarray(features, dtype="<f4").tobytes()
                filename = panel["panel_id"] + ".features.f32"
                with (output / filename).open("xb") as stream:
                    stream.write(raw)
                rows.append(
                    {
                        "split": "train",
                        "panel_id": panel["panel_id"],
                        "input_sha256": panel["tensor"]["sha256"],
                        "input_shape": list(values.shape),
                        "feature_file": filename,
                        "feature_shape": list(features.shape),
                        "feature_sha256": sha256(raw).hexdigest(),
                        "feature_byte_count": len(raw),
                        "feature_dtype": "float32-le",
                        "maximum_absolute_error": float(np.max(np.abs(expected - actual))),
                    }
                )
        patch = head_api.patch_head_constants(
            parent, output / "no-op-detector.onnx", bundle.head
        )
        if patch.output_sha256 != head_api.REVIEWED_DETECTOR_SHA256 or patch.changed_constants:
            raise SupplementalFeatureError("No-op export differs from reviewed parent")
        if (
            _digest(helper_path) != helper_sha256
            or _digest(source_path) != source_sha256
            or _digest(_inside(root, COMPOSITION_PATH)) != COMPOSITION_SHA256
            or _digest(_inside(root, CAPTURE_PATH)) != CAPTURE_SHA256
            or _digest(parent) != head_api.REVIEWED_DETECTOR_SHA256
        ):
            raise SupplementalFeatureError("Execution source changed during capture")
        maximum = max(row["maximum_absolute_error"] for row in rows)
        report = {
            "schema": SCHEMA,
            "scope": composition["scope"],
            "composition_report_sha256": COMPOSITION_SHA256,
            "capture_report_sha256": CAPTURE_SHA256,
            "capture_request_sha256": REQUEST_SHA256,
            "capture_source_sha256": CAPTURE_SOURCE_SHA256,
            "historical_capture_sha256": HISTORICAL_CAPTURE_SHA256,
            "capture_assemblies": [
                {"name": name, "path": path.as_posix(), "sha256": digest}
                for name, path, digest in ASSEMBLIES
            ],
            "source_model_sha256": head_api.REVIEWED_DETECTOR_SHA256,
            "source_code_sha256": source_sha256,
            "helper_sha256": helper_sha256,
            "feature_model_sha256": feature_model_sha256,
            "no_op_model_sha256": patch.output_sha256,
            "provider": "CPUExecutionProvider",
            "onnxruntime_version": ort.__version__,
            "torch_version": torch.__version__,
            "torch_head_arithmetic": "float64",
            "cpu_threads": 1,
            "torch_mkldnn_enabled": False,
            "graph_optimization": "ORT_ENABLE_ALL",
            "panels": rows,
            "panel_count": len(rows),
            "train_panel_count": len(rows),
            "development_panel_count": 0,
            "maximum_absolute_error": maximum,
            "tolerance": TOLERANCE,
            "passed": maximum <= TOLERANCE,
            "optimizer_steps": 0,
            "private_reads": 0,
            "sealed_reads": 0,
            "production_approved": False,
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
