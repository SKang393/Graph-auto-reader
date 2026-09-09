# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Exact production DB tensors and full-source text supervision.

The C# capture request produced here is annotation-free.  Synthetic truth is
regenerated only in this Python evaluator and is joined to authenticated tensor
captures after the C# production preprocessing path has completed.
"""

from __future__ import annotations

from dataclasses import dataclass
from hashlib import sha256
import json
import math
from pathlib import Path
from typing import Any, Mapping, Sequence

import numpy as np
from PIL import Image

from ml.markers.gate_seal import canonical_json_bytes
from ml.markers.center.mask_preserving_v24 import runtime_inputs
from ml.markers.center.plot_domain_v25 import runtime_domain_binding
from ml.markers.center.plot_domain_v25 import runtime_domain_binding_v3
from ml.ocr import production_tiled_inputs


REPOSITORY_ROOT = Path(__file__).resolve().parents[3]
V3_BINDING_SHA256 = "bcf821a2aaadc5ee8e18bd97dbe0488d120d2ac974a249c00d76b6cfccf0d98a"
CAPTURE_REQUEST_SCHEMA = "graphreader.official-head-tensor-capture-request.v1"
CAPTURE_REPORT_SCHEMA = "graphreader.official-head-tensor-capture-report.v1"
CAPTURE_SCOPE = "project-owned-synthetic-train-dev-model-free"
MAXIMUM_SIDE_LENGTH = 960
DIMENSION_MULTIPLE = 128
DB_SHRINK_RATIO = 0.4
DETECTOR_CONFIGURATION_FINGERPRINT = (
    "7a8eb59f3b6980096e80247a6b195e25b3e0b887240e700aaf77cf5a1d64bc81"
)
CAPTURE_SOURCE_PATH = Path(
    "tools/GraphReader.SyntheticRuntimeEvidence/OfficialHeadTensorCapture.cs"
)
CAPTURE_ASSEMBLY_NAMES = (
    "GraphReader.SyntheticRuntimeEvidence",
    "GraphReader.App",
    "GraphReader.Ocr",
    "GraphReader.Inference",
)


class ProductionHeadInputError(ValueError):
    """The authenticated panel, capture, or supervision geometry is invalid."""


@dataclass(frozen=True)
class CapturePanelInput:
    split: str
    source_sha256: str
    panel_id: str
    panel_sha256: str
    width: int
    height: int
    crop: tuple[int, int, int, int]
    report_path: Path
    report_sha256: str
    panel_png_path: Path
    panel_png_sha256: str
    panel_png_byte_count: int
    recorded_unmasked_gray_sha256: str
    reconstructed_bgr_sha256: str


@dataclass(frozen=True)
class CapturePreparation:
    binding_sha256: str
    tiled_inputs: production_tiled_inputs.ProductionTiledInputs
    panels: tuple[CapturePanelInput, ...]
    request: Mapping[str, Any]


@dataclass(frozen=True)
class DegenerateSupervisedRegion:
    truth_id: str
    source_text_id: str
    panel_id: str
    tensor_box: tuple[float, float, float, float]
    reason: str


@dataclass(frozen=True)
class ProductionHeadPanel:
    split: str
    source_sha256: str
    panel_id: str
    panel_sha256: str
    width: int
    height: int
    crop: tuple[int, int, int, int]
    tensor_shape: tuple[int, int, int, int]
    input_values: np.ndarray
    shrink_target: np.ndarray
    supervision_mask: np.ndarray
    projections: tuple[production_tiled_inputs.PanelTextProjection, ...]
    degenerate_regions: tuple[DegenerateSupervisedRegion, ...]
    tensor_sha256: str
    reconstructed_bgr_sha256: str


@dataclass(frozen=True)
class ProductionHeadSplit:
    name: str
    source_count: int
    panel_count: int
    full_source_truth_count: int
    projected_source_truth_count: int
    outside_runtime_crop_truth_count: int
    partial_source_truth_count: int
    overlapping_source_truth_count: int
    degenerate_supervised_region_count: int
    degenerate_source_truth_count: int
    source_truths: tuple[production_tiled_inputs.SourceTextTruth, ...]
    panels: tuple[ProductionHeadPanel, ...]


@dataclass(frozen=True)
class ProductionHeadInputs:
    binding_sha256: str
    capture_report_sha256: str
    train: ProductionHeadSplit
    dev: ProductionHeadSplit


def prepare_capture_request(
    binding_path: Path,
    expected_binding_sha256: str = V3_BINDING_SHA256,
    *,
    repository_root: Path = REPOSITORY_ROOT,
    capture_binary_root: Path | None = None,
    capture_source_path: Path | None = None,
) -> CapturePreparation:
    """Authenticate V3 evidence and build an annotation-free 37-panel request."""

    if expected_binding_sha256.lower() != V3_BINDING_SHA256:
        raise ProductionHeadInputError("the production-head bridge requires the frozen V3 binding")
    root = repository_root.resolve()
    binding = runtime_domain_binding_v3.load_runtime_domain_binding_v3(
        binding_path, expected_binding_sha256, repository_root=root
    )
    if binding.failures:
        raise ProductionHeadInputError("V3 runtime binding retains failed panels")

    candidate_path, candidate_payload = runtime_inputs._read_bound_artifact(
        binding.implementation.candidate_path,
        binding.implementation.candidate_sha256,
        root,
        "V3 runtime candidate",
    )
    candidate = _json_object(candidate_payload, "V3 runtime candidate")
    detector = _object(candidate.get("detector"), "candidate detector")
    detector_manifest_path = Path(_string(detector, "manifest_path")).resolve()
    detector_manifest_sha = _sha(detector.get("manifest_sha256"), "detector manifest")
    runtime_inputs._read_bound_artifact(
        detector_manifest_path, detector_manifest_sha, root, "detector manifest"
    )
    if (
        _string(detector, "model_id") != "PP-OCRv5_mobile_det"
        or _string(detector, "model_version") != "5.0.0"
        or _sha(detector.get("model_sha256"), "detector model")
        != "d4aa24d408cd70b8b9f66cc758e20f397fc31a9c69d8477cf8887fc53bd5fceb"
    ):
        raise ProductionHeadInputError("candidate detector identity is not the pinned official head")

    bindings = (*binding.train_bindings, binding.dev_binding)
    source_path = _inside(root, capture_source_path or root / CAPTURE_SOURCE_PATH)
    source_sha = sha256(source_path.read_bytes()).hexdigest()
    binary_root = _inside(
        root,
        capture_binary_root
        or root / "tools/GraphReader.SyntheticRuntimeEvidence/bin/Release/net10.0-windows/win-x64",
    )
    assemblies = []
    for name in CAPTURE_ASSEMBLY_NAMES:
        path = _inside(root, binary_root / f"{name}.dll")
        payload = path.read_bytes()
        assemblies.append(
            {"name": name, "path": _repository_path(path, root), "sha256": sha256(payload).hexdigest()}
        )
    panels = _capture_panels(binding, root)

    # Truth regeneration starts only after every annotation-free input,
    # implementation byte, candidate, and detector manifest has authenticated.
    train = production_tiled_inputs._build_split(
        "train", binding.train, binding.profile.train,
        production_tiled_inputs.EXPECTED_TRAIN_SOURCE_COUNT,
        production_tiled_inputs.EXPECTED_TRAIN_PANEL_COUNT,
        production_tiled_inputs.EXPECTED_TRAIN_TEXT_TRUTH_COUNT,
    )
    dev = production_tiled_inputs._build_split(
        "validation", binding.dev, binding.profile.dev,
        production_tiled_inputs.EXPECTED_DEV_SOURCE_COUNT,
        production_tiled_inputs.EXPECTED_DEV_PANEL_COUNT,
        production_tiled_inputs.EXPECTED_DEV_TEXT_TRUTH_COUNT,
    )
    tiled = production_tiled_inputs.ProductionTiledInputs(binding.binding_sha256, train, dev)
    expected_ids = {panel.panel_id for panel in (*train.panels, *dev.panels)}
    if {panel.panel_id for panel in panels} != expected_ids or len(panels) != 37:
        raise ProductionHeadInputError("capture request does not retain the exact 37 V3 panels")

    request: dict[str, Any] = {
        "schema": CAPTURE_REQUEST_SCHEMA,
        "scope": CAPTURE_SCOPE,
        "synthetic_only": True,
        "private_data": False,
        "sealed_data": False,
        "truth_included": False,
        "model_inference": False,
        "training_input_ready": False,
        "production_approved": False,
        "capture_source": {
            "path": _repository_path(source_path, root),
            "sha256": source_sha,
        },
        "assemblies": assemblies,
        "binding": {
            "path": _repository_path(binding_path, root),
            "sha256": binding.binding_sha256,
        },
        "candidate": {
            "path": _repository_path(candidate_path, root),
            "sha256": binding.implementation.candidate_sha256,
        },
        "detector": {
            "model_path": _string(detector, "model_path"),
            "model_id": _string(detector, "model_id"),
            "model_version": _string(detector, "model_version"),
            "model_sha256": _sha(detector.get("model_sha256"), "detector model"),
            "manifest_path": _repository_path(detector_manifest_path, root),
            "manifest_sha256": detector_manifest_sha,
        },
        "native": {
            "path": _string(candidate, "native_path"),
            "sha256": _sha(candidate.get("native_sha256"), "native runtime"),
            "scope": _string(candidate, "native_scope"),
        },
        "license_inputs": candidate.get("license_inputs"),
        "maximum_side_length": MAXIMUM_SIDE_LENGTH,
        "dimension_multiple": DIMENSION_MULTIPLE,
        "detector_configuration_fingerprint": DETECTOR_CONFIGURATION_FINGERPRINT,
        "reports": [
            {
                "split": item.split,
                "manifest_path": _repository_path(item.manifest_path, root),
                "manifest_sha256": item.manifest_sha256,
                "report_path": _repository_path(item.report_path, root),
                "report_sha256": item.report_sha256,
            }
            for item in bindings
        ],
        "panels": [
            {
                "split": item.split,
                "source_sha256": item.source_sha256,
                "panel_id": item.panel_id,
                "panel_sha256": item.panel_sha256,
                "width": item.width,
                "height": item.height,
                "crop": list(item.crop),
                "report_path": _repository_path(item.report_path, root),
                "report_sha256": item.report_sha256,
                "panel_png": {
                    "path": _repository_path(item.panel_png_path, root),
                    "sha256": item.panel_png_sha256,
                    "byte_count": item.panel_png_byte_count,
                },
                "recorded_unmasked_gray_sha256": item.recorded_unmasked_gray_sha256,
                "reconstructed_bgr_sha256": item.reconstructed_bgr_sha256,
                "bgr_identity_kind": "reconstructed_from_authenticated_panel_png",
            }
            for item in panels
        ],
    }
    return CapturePreparation(binding.binding_sha256, tiled, panels, request)


def write_capture_request(path: Path, preparation: CapturePreparation) -> str:
    payload = canonical_json_bytes(preparation.request)
    path.parent.mkdir(parents=True, exist_ok=True)
    with path.open("xb") as stream:
        stream.write(payload)
    return sha256(payload).hexdigest()


def load_production_head_inputs(
    binding_path: Path,
    expected_binding_sha256: str,
    capture_report_path: Path,
    expected_capture_report_sha256: str,
    *,
    repository_root: Path = REPOSITORY_ROOT,
) -> ProductionHeadInputs:
    """Load exact captured tensors, then attach independently regenerated truth."""

    root = repository_root.resolve()
    report_path, report_payload = runtime_inputs._read_bound_artifact(
        capture_report_path,
        expected_capture_report_sha256,
        root,
        "official-head tensor capture report",
    )
    report = _json_object(report_payload, "official-head tensor capture report")
    request_descriptor = _object(report.get("request"), "capture request identity")
    request_path = _inside(root, root / _string(request_descriptor, "path"))
    request_payload = _read_exact(
        request_path,
        _sha(request_descriptor.get("sha256"), "capture request"),
        None,
        "capture request",
    )
    request = _json_object(request_payload, "capture request")
    source_descriptor = _object(request.get("capture_source"), "capture source")
    source_path = _inside(root, root / _string(source_descriptor, "path"))
    assembly_records = _array(request.get("assemblies"), "capture assemblies")
    if len(assembly_records) != len(CAPTURE_ASSEMBLY_NAMES):
        raise ProductionHeadInputError("capture request assembly inventory is incomplete")
    assembly_paths = [
        _inside(root, root / _string(_object(item, "capture assembly"), "path"))
        for item in assembly_records
    ]
    binary_roots = {path.parent for path in assembly_paths}
    if len(binary_roots) != 1:
        raise ProductionHeadInputError("capture assemblies do not share one immutable snapshot")
    preparation = prepare_capture_request(
        binding_path,
        expected_binding_sha256,
        repository_root=root,
        capture_binary_root=next(iter(binary_roots)),
        capture_source_path=source_path,
    )
    _validate_report_header(report, preparation, root)
    records = report.get("panels")
    if not isinstance(records, list) or len(records) != 37:
        raise ProductionHeadInputError("tensor capture report must retain exactly 37 panels")
    by_id: dict[str, Mapping[str, Any]] = {}
    for raw in records:
        record = _object(raw, "tensor capture panel")
        panel_id = _string(record, "panel_id")
        if panel_id in by_id:
            raise ProductionHeadInputError("tensor capture report repeats a panel identity")
        by_id[panel_id] = record

    request_panels = {panel.panel_id: panel for panel in preparation.panels}
    if set(by_id) != set(request_panels):
        raise ProductionHeadInputError("tensor capture report panel inventory changed")
    train = _load_split(preparation.tiled_inputs.train, by_id, request_panels, report_path, root)
    dev = _load_split(preparation.tiled_inputs.dev, by_id, request_panels, report_path, root)
    return ProductionHeadInputs(
        preparation.binding_sha256,
        expected_capture_report_sha256.lower(),
        train,
        dev,
    )


def _capture_panels(
    binding: runtime_domain_binding_v3.RuntimeDomainBindingV3,
    root: Path,
) -> tuple[CapturePanelInput, ...]:
    runtime_panels = {
        panel.runtime_input.panel_id: panel.runtime_input
        for panel in (*binding.train, *binding.dev)
    }
    output: list[CapturePanelInput] = []
    for descriptor in (*binding.train_bindings, binding.dev_binding):
        report_path, payload = runtime_inputs._read_bound_artifact(
            descriptor.report_path, descriptor.report_sha256, root, "V3 runtime report"
        )
        report = _json_object(payload, "V3 runtime report")
        for case in _array(report.get("cases"), "runtime cases"):
            case_object = _object(case, "runtime case")
            source_sha = _sha(case_object.get("image_sha256"), "runtime source")
            for raw_panel in _array(case_object.get("panels"), "runtime panels"):
                panel = _object(raw_panel, "runtime panel")
                panel_id = _string(panel, "panel_id")
                runtime = runtime_panels.get(panel_id)
                if runtime is None or runtime.source_sha256 != source_sha:
                    raise ProductionHeadInputError("runtime report contains an unbound panel")
                if _string(panel, "status") != "seed-completed":
                    raise ProductionHeadInputError("production-head capture requires complete panels")
                png = _object(panel.get("panel_png"), "runtime panel PNG")
                png_path = report_path.parent / source_sha / panel_id / _string(png, "file")
                encoded = _read_exact(
                    png_path,
                    _sha(png.get("sha256"), "panel PNG"),
                    int(png.get("byte_count", -1)),
                    "panel PNG",
                )
                gray, bgr = _decode_production_pixels(encoded, runtime.width, runtime.height)
                recorded_gray = _sha(
                    _object(panel.get("ocr_proposal_diagnostic"), "OCR diagnostic").get(
                        "unmasked_input_sha256"
                    ),
                    "recorded unmasked Gray8",
                )
                if sha256(gray.tobytes(order="C")).hexdigest() != recorded_gray:
                    raise ProductionHeadInputError(
                        "decoded original Gray8 differs from its recorded OCR invocation"
                    )
                if not np.array_equal(gray, runtime.gray8):
                    raise ProductionHeadInputError("decoded original Gray8 differs from V3 input")
                output.append(
                    CapturePanelInput(
                        descriptor.split,
                        source_sha,
                        panel_id,
                        runtime.panel_sha256,
                        runtime.width,
                        runtime.height,
                        runtime.crop,
                        report_path,
                        descriptor.report_sha256,
                        png_path,
                        _sha(png.get("sha256"), "panel PNG"),
                        len(encoded),
                        recorded_gray,
                        sha256(bgr.tobytes(order="C")).hexdigest(),
                    )
                )
    result = tuple(sorted(output, key=lambda item: (item.split, item.panel_id)))
    if len({item.panel_id for item in result}) != len(result):
        raise ProductionHeadInputError("capture request repeats panel identities")
    return result


def _decode_production_pixels(
    encoded: bytes, width: int, height: int
) -> tuple[np.ndarray, np.ndarray]:
    from io import BytesIO

    with Image.open(BytesIO(encoded)) as image:
        image.load()
        if image.size != (width, height):
            raise ProductionHeadInputError("decoded panel dimensions differ from runtime evidence")
        rgba = np.asarray(image.convert("RGBA"), dtype=np.uint8).astype(np.uint16)
    alpha = rgba[:, :, 3]
    red = ((rgba[:, :, 0] * alpha + 255 * (255 - alpha) + 127) // 255).astype(np.uint8)
    green = ((rgba[:, :, 1] * alpha + 255 * (255 - alpha) + 127) // 255).astype(np.uint8)
    blue = ((rgba[:, :, 2] * alpha + 255 * (255 - alpha) + 127) // 255).astype(np.uint8)
    gray = ((299 * red.astype(np.uint32) + 587 * green.astype(np.uint32) +
             114 * blue.astype(np.uint32) + 500) // 1000).astype(np.uint8)
    bgr = np.stack((blue, green, red), axis=2)
    return _immutable(gray, np.uint8, (height, width)), _immutable(
        bgr, np.uint8, (height, width, 3)
    )


def _validate_report_header(
    report: Mapping[str, Any], preparation: CapturePreparation, root: Path
) -> None:
    expected = {
        "schema": CAPTURE_REPORT_SCHEMA,
        "scope": CAPTURE_SCOPE,
        "synthetic_only": True,
        "private_data": False,
        "sealed_data": False,
        "truth_used_by_capture": False,
        "model_inference": False,
        "training_input_ready": False,
        "production_approved": False,
        "binding_sha256": preparation.binding_sha256,
        "maximum_side_length": MAXIMUM_SIDE_LENGTH,
        "dimension_multiple": DIMENSION_MULTIPLE,
        "detector_configuration_fingerprint": DETECTOR_CONFIGURATION_FINGERPRINT,
        "panel_count": 37,
        "failed_panel_count": 0,
        "candidate_sha256": preparation.request["candidate"]["sha256"],
        "detector_model_sha256": preparation.request["detector"]["model_sha256"],
        "detector_manifest_sha256": preparation.request["detector"]["manifest_sha256"],
        "native_sha256": preparation.request["native"]["sha256"],
        "capture_source_sha256": preparation.request["capture_source"]["sha256"],
    }
    for key, value in expected.items():
        if report.get(key) != value:
            raise ProductionHeadInputError(f"tensor capture report has invalid {key}")
    if report.get("assemblies") != preparation.request["assemblies"]:
        raise ProductionHeadInputError("tensor capture report execution assemblies changed")
    fingerprint = _sha(report.get("detector_configuration_fingerprint"), "detector fingerprint")
    request = _object(report.get("request"), "capture request identity")
    request_path = _inside(root, root / _string(request, "path"))
    request_payload = _read_exact(
        request_path, _sha(request.get("sha256"), "capture request"), None, "capture request"
    )
    if json.loads(request_payload) != preparation.request:
        raise ProductionHeadInputError("capture request differs from authenticated V3 preparation")
    for record in _array(report.get("panels"), "tensor capture panels"):
        if _sha(_object(record, "tensor capture panel").get(
            "detector_configuration_fingerprint"
        ), "panel detector fingerprint") != fingerprint:
            raise ProductionHeadInputError("panel detector fingerprint changed within capture")


def _load_split(
    tiled: production_tiled_inputs.ProductionTiledSplit,
    records: Mapping[str, Mapping[str, Any]],
    request_panels: Mapping[str, CapturePanelInput],
    report_path: Path,
    root: Path,
) -> ProductionHeadSplit:
    panels: list[ProductionHeadPanel] = []
    for panel in tiled.panels:
        capture = records[panel.panel_id]
        expected = request_panels[panel.panel_id]
        if (
            _string(capture, "split") != panel.split
            or _sha(capture.get("source_sha256"), "capture source") != panel.source_sha256
            or _sha(capture.get("panel_sha256"), "capture panel") != panel.panel_sha256
            or int(capture.get("width", -1)) != panel.width
            or int(capture.get("height", -1)) != panel.height
            or tuple(capture.get("crop", ())) != panel.crop
            or _sha(capture.get("recorded_unmasked_gray_sha256"), "capture Gray8")
            != expected.recorded_unmasked_gray_sha256
            or _sha(capture.get("reconstructed_bgr_sha256"), "capture BGR24")
            != expected.reconstructed_bgr_sha256
            or capture.get("bgr_identity_kind") != "reconstructed_from_authenticated_panel_png"
        ):
            raise ProductionHeadInputError("captured tensor panel identity changed")
        tensor = _object(capture.get("tensor"), "captured tensor")
        shape_raw = tensor.get("shape")
        if (
            not isinstance(shape_raw, list)
            or len(shape_raw) != 4
            or any(type(value) is not int or value <= 0 for value in shape_raw)
        ):
            raise ProductionHeadInputError("captured tensor shape is invalid")
        shape = tuple(shape_raw)
        expected_shape = _production_tensor_shape(panel.width, panel.height)
        if (
            shape != expected_shape
            or tensor.get("dtype") != "float32-le"
            or not isinstance(tensor.get("input_name"), str)
            or not tensor.get("input_name")
            or not isinstance(tensor.get("output_name"), str)
            or not tensor.get("output_name")
        ):
            raise ProductionHeadInputError("captured tensor is not production NCHW DB input")
        tensor_path = _inside(report_path.parent, report_path.parent / _string(tensor, "file"))
        payload = _read_exact(
            tensor_path,
            _sha(tensor.get("sha256"), "captured tensor"),
            int(tensor.get("byte_count", -1)),
            "captured tensor",
        )
        if len(payload) != math.prod(shape) * 4:
            raise ProductionHeadInputError("captured tensor byte count differs from its shape")
        values = np.frombuffer(payload, dtype="<f4").reshape(shape)
        values = _immutable(values, np.float32, shape)
        if not np.isfinite(values).all():
            raise ProductionHeadInputError("captured tensor contains non-finite values")
        target, mask, degenerates = _build_supervision(panel, shape[3], shape[2])
        panels.append(
            ProductionHeadPanel(
                panel.split,
                panel.source_sha256,
                panel.panel_id,
                panel.panel_sha256,
                panel.width,
                panel.height,
                panel.crop,
                shape,
                values,
                target,
                mask,
                panel.projections,
                degenerates,
                _sha(tensor.get("sha256"), "captured tensor"),
                expected.reconstructed_bgr_sha256,
            )
        )
    degenerate_truths = {
        item.truth_id for panel in panels for item in panel.degenerate_regions
    }
    return ProductionHeadSplit(
        tiled.name,
        tiled.source_count,
        tiled.panel_count,
        tiled.full_source_truth_count,
        tiled.projected_source_truth_count,
        tiled.outside_runtime_crop_truth_count,
        tiled.partial_source_truth_count,
        tiled.overlapping_source_truth_count,
        sum(len(panel.degenerate_regions) for panel in panels),
        len(degenerate_truths),
        tiled.source_truths,
        tuple(panels),
    )


def _build_supervision(
    panel: production_tiled_inputs.ProductionTiledPanel,
    tensor_width: int,
    tensor_height: int,
) -> tuple[np.ndarray, np.ndarray, tuple[DegenerateSupervisedRegion, ...]]:
    target = np.zeros((1, tensor_height, tensor_width), dtype=np.float32)
    mask = np.ones((1, tensor_height, tensor_width), dtype=np.float32)
    degenerate: list[DegenerateSupervisedRegion] = []
    scale_x = tensor_width / panel.width
    scale_y = tensor_height / panel.height
    for projection in panel.projections:
        box = (
            projection.panel_box[0] * scale_x,
            projection.panel_box[1] * scale_y,
            projection.panel_box[2] * scale_x,
            projection.panel_box[3] * scale_y,
        )
        width = box[2] - box[0]
        height = box[3] - box[1]
        distance = width * height * (1.0 - DB_SHRINK_RATIO**2) / (2.0 * (width + height))
        left = int(math.ceil(box[0] + distance))
        top = int(math.ceil(box[1] + distance))
        right = int(math.floor(box[2] - distance))
        bottom = int(math.floor(box[3] - distance))
        left = min(max(0, left), tensor_width)
        right = min(max(0, right), tensor_width)
        top = min(max(0, top), tensor_height)
        bottom = min(max(0, bottom), tensor_height)
        if right <= left or bottom <= top:
            ignore_left = min(max(0, int(math.floor(box[0]))), tensor_width)
            ignore_top = min(max(0, int(math.floor(box[1]))), tensor_height)
            ignore_right = min(max(0, int(math.ceil(box[2]))), tensor_width)
            ignore_bottom = min(max(0, int(math.ceil(box[3]))), tensor_height)
            if ignore_right > ignore_left and ignore_bottom > ignore_top:
                mask[0, ignore_top:ignore_bottom, ignore_left:ignore_right] = 0
            degenerate.append(
                DegenerateSupervisedRegion(
                    projection.truth_id,
                    projection.source_text_id,
                    projection.panel_id,
                    box,
                    "db_shrink_collapsed_after_production_resize",
                )
            )
            continue
        target[0, top:bottom, left:right] = 1
    mask[target > 0] = 1
    return (
        _immutable(target, np.float32, target.shape),
        _immutable(mask, np.float32, mask.shape),
        tuple(degenerate),
    )


def _production_tensor_shape(width: int, height: int) -> tuple[int, int, int, int]:
    if type(width) is not int or type(height) is not int or width <= 0 or height <= 0:
        raise ProductionHeadInputError("production tensor source dimensions are invalid")
    source_width = max(32, width) if width + height < 64 else width
    source_height = max(32, height) if width + height < 64 else height
    ratio = MAXIMUM_SIDE_LENGTH / max(source_width, source_height)
    resized_width = int(source_width * ratio)
    resized_height = int(source_height * ratio)
    if resized_width <= 0 or resized_height <= 0:
        raise ProductionHeadInputError("production DB resize produced a zero dimension")
    target_width = ((resized_width + DIMENSION_MULTIPLE - 1) // DIMENSION_MULTIPLE) * DIMENSION_MULTIPLE
    target_height = ((resized_height + DIMENSION_MULTIPLE - 1) // DIMENSION_MULTIPLE) * DIMENSION_MULTIPLE
    return (1, 3, target_height, target_width)


def _read_exact(
    path: Path, expected_sha256: str, expected_byte_count: int | None, label: str
) -> bytes:
    payload = path.read_bytes()
    if expected_byte_count is not None and len(payload) != expected_byte_count:
        raise ProductionHeadInputError(f"{label} byte count changed")
    if sha256(payload).hexdigest() != expected_sha256.lower():
        raise ProductionHeadInputError(f"{label} checksum changed")
    return payload


def _immutable(array: np.ndarray, dtype: Any, shape: tuple[int, ...]) -> np.ndarray:
    contiguous = np.ascontiguousarray(array, dtype=dtype)
    if contiguous.shape != shape:
        raise ProductionHeadInputError("prepared array shape changed")
    return np.frombuffer(contiguous.tobytes(order="C"), dtype=contiguous.dtype).reshape(shape)


def _inside(root: Path, path: Path) -> Path:
    resolved_root = root.resolve()
    resolved = path.resolve()
    try:
        resolved.relative_to(resolved_root)
    except ValueError as exception:
        raise ProductionHeadInputError("artifact path escapes its authenticated root") from exception
    return resolved


def _repository_path(path: Path, root: Path) -> str:
    return _inside(root, path).relative_to(root).as_posix()


def _json_object(payload: bytes, label: str) -> Mapping[str, Any]:
    try:
        value = json.loads(payload)
    except (UnicodeDecodeError, json.JSONDecodeError) as exception:
        raise ProductionHeadInputError(f"{label} is not valid JSON") from exception
    return _object(value, label)


def _object(value: Any, label: str) -> Mapping[str, Any]:
    if not isinstance(value, dict):
        raise ProductionHeadInputError(f"{label} must be an object")
    return value


def _array(value: Any, label: str) -> Sequence[Any]:
    if not isinstance(value, list):
        raise ProductionHeadInputError(f"{label} must be an array")
    return value


def _string(value: Mapping[str, Any], key: str) -> str:
    result = value.get(key)
    if not isinstance(result, str) or not result:
        raise ProductionHeadInputError(f"{key} must be a nonempty string")
    return result


def _sha(value: Any, label: str) -> str:
    if not isinstance(value, str) or len(value) != 64 or any(
        character not in "0123456789abcdefABCDEF" for character in value
    ):
        raise ProductionHeadInputError(f"{label} must be a SHA-256")
    return value.lower()


__all__ = [
    "CAPTURE_REPORT_SCHEMA",
    "CAPTURE_REQUEST_SCHEMA",
    "CAPTURE_SCOPE",
    "CapturePreparation",
    "DB_SHRINK_RATIO",
    "DegenerateSupervisedRegion",
    "ProductionHeadInputError",
    "ProductionHeadInputs",
    "ProductionHeadPanel",
    "ProductionHeadSplit",
    "V3_BINDING_SHA256",
    "load_production_head_inputs",
    "prepare_capture_request",
    "write_capture_request",
]
