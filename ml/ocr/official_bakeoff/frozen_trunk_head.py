# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Frozen PP-OCRv5 ONNX trunk and trainable DB shrink-map head utilities.

This module does not authorize training or production use.  It extracts the
small inference head from the exact reviewed detector, keeps the backbone and
RSEFPN inside ONNX Runtime, and can patch only the explicitly allowlisted head
constants into a copy of the original graph.
"""

from __future__ import annotations

from dataclasses import dataclass
from hashlib import sha256
from pathlib import Path
import tempfile
from typing import Iterable, Mapping, Sequence

import numpy as np
import onnx
from onnx import TensorProto, helper, numpy_helper
import torch
from torch import Tensor, nn
from torch.nn import functional as F


REVIEWED_DETECTOR_SHA256 = (
    "d4aa24d408cd70b8b9f66cc758e20f397fc31a9c69d8477cf8887fc53bd5fceb"
)
MODEL_INPUT_NAME = "x"
MODEL_OUTPUT_NAME = "fetch_name_0"
FEATURE_OUTPUT_NAME = "p2o.pd_op.concat.0.0"
FEATURE_CHANNELS = 96
HEAD_CHANNELS = 24
ONNX_OPSET = 11
ONNX_IR_VERSION = 6
BN_EPSILON = 9.999999747378752e-06
BN_MOMENTUM = 0.8999999761581421

TRAINING_TARGET_CONTRACT = (
    "one-channel DB shrink-map target aligned to fetch_name_0; V38 Gray8 tiles "
    "and filled truth-box targets are incompatible"
)

TRAINABLE_CONSTANT_SHAPES: Mapping[str, tuple[int, ...]] = {
    "conv2d_159.w_0": (24, 96, 3, 3),
    "batch_norm_0.w_0": (24,),
    "batch_norm_0.b_0": (24,),
    "conv2d_transpose_0.w_0": (24, 24, 2, 2),
    "conv2d_transpose_0.b_0": (24,),
    "batch_norm_1.w_0": (24,),
    "batch_norm_1.b_0": (24,),
    "conv2d_transpose_1.w_0": (24, 1, 2, 2),
    "conv2d_transpose_1.b_0": (1,),
}

FROZEN_BATCH_NORM_SHAPES: Mapping[str, tuple[int, ...]] = {
    "batch_norm_0.w_1": (24,),
    "batch_norm_0.w_2": (24,),
    "batch_norm_1.w_1": (24,),
    "batch_norm_1.w_2": (24,),
}

_PARAMETER_TO_CONSTANT: Mapping[str, str] = {
    "conv_weight": "conv2d_159.w_0",
    "bn0_weight": "batch_norm_0.w_0",
    "bn0_bias": "batch_norm_0.b_0",
    "deconv0_weight": "conv2d_transpose_0.w_0",
    "deconv0_bias": "conv2d_transpose_0.b_0",
    "bn1_weight": "batch_norm_1.w_0",
    "bn1_bias": "batch_norm_1.b_0",
    "deconv1_weight": "conv2d_transpose_1.w_0",
    "deconv1_bias": "conv2d_transpose_1.b_0",
}

_BUFFER_TO_CONSTANT: Mapping[str, str] = {
    "bn0_running_mean": "batch_norm_0.w_1",
    "bn0_running_variance": "batch_norm_0.w_2",
    "bn1_running_mean": "batch_norm_1.w_1",
    "bn1_running_variance": "batch_norm_1.w_2",
}


class FrozenTrunkHeadError(RuntimeError):
    """Raised when graph identity, topology, or patch containment is invalid."""


@dataclass(frozen=True)
class ConstantIdentity:
    name: str
    shape: tuple[int, ...]
    dtype: str
    sha256: str
    trainable: bool


@dataclass(frozen=True)
class FrozenTrunkHeadMetadata:
    source_sha256: str
    input_name: str
    feature_output_name: str
    output_name: str
    opset: int
    ir_version: int
    trainable_parameter_count: int
    trainable_constants: tuple[ConstantIdentity, ...]
    frozen_batch_norm_constants: tuple[ConstantIdentity, ...]
    target_contract: str


@dataclass(frozen=True)
class FrozenTrunkHeadBundle:
    metadata: FrozenTrunkHeadMetadata
    head: "FrozenDbHead"


@dataclass(frozen=True)
class PatchResult:
    source_sha256: str
    output_sha256: str
    changed_constants: tuple[str, ...]
    unchanged_trainable_constants: tuple[str, ...]


@dataclass(frozen=True)
class StepZeroParityResult:
    source_sha256: str
    feature_model_sha256: str
    provider: str
    torch_backend: str
    errors_by_shape: tuple[tuple[tuple[int, int, int, int], float], ...]
    maximum_absolute_error: float
    tolerance: float
    passed: bool


def sha256_file(path: Path) -> str:
    digest = sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def _require_sha256(value: str, label: str) -> None:
    if len(value) != 64 or value != value.lower() or any(character not in "0123456789abcdef" for character in value):
        raise FrozenTrunkHeadError(f"{label} must be a lowercase SHA-256 value")


def _load_reviewed_model(path: Path, expected_sha256: str) -> tuple[bytes, onnx.ModelProto]:
    _require_sha256(expected_sha256, "Expected detector identity")
    source_bytes = path.read_bytes()
    observed = sha256(source_bytes).hexdigest()
    if observed != expected_sha256:
        raise FrozenTrunkHeadError(
            f"Detector SHA-256 changed: expected {expected_sha256}, observed {observed}"
        )
    try:
        model = onnx.load_model_from_string(source_bytes)
        onnx.checker.check_model(model, full_check=True)
    except Exception as error:
        raise FrozenTrunkHeadError("Detector is not a valid self-contained ONNX model") from error
    if model.ir_version != ONNX_IR_VERSION:
        raise FrozenTrunkHeadError(f"Detector IR version must be {ONNX_IR_VERSION}")
    opsets = {entry.domain: entry.version for entry in model.opset_import}
    if opsets != {"": ONNX_OPSET}:
        raise FrozenTrunkHeadError(f"Detector opset imports changed: {opsets}")
    if model.graph.initializer or model.graph.sparse_initializer:
        raise FrozenTrunkHeadError("Reviewed detector weights must remain graph Constants")
    _validate_io(model)
    _validate_head_topology(model)
    return source_bytes, model


def _validate_io(model: onnx.ModelProto) -> None:
    if len(model.graph.input) != 1 or model.graph.input[0].name != MODEL_INPUT_NAME:
        raise FrozenTrunkHeadError("Detector input identity changed")
    if len(model.graph.output) != 1 or model.graph.output[0].name != MODEL_OUTPUT_NAME:
        raise FrozenTrunkHeadError("Detector output identity changed")
    input_type = model.graph.input[0].type.tensor_type
    output_type = model.graph.output[0].type.tensor_type
    if input_type.elem_type != TensorProto.FLOAT or output_type.elem_type != TensorProto.FLOAT:
        raise FrozenTrunkHeadError("Detector input and output must be float32 tensors")
    input_dims = input_type.shape.dim
    output_dims = output_type.shape.dim
    if len(input_dims) != 4 or not input_dims[1].HasField("dim_value") or input_dims[1].dim_value != 3:
        raise FrozenTrunkHeadError("Detector input must be dynamic NCHW with three channels")
    if len(output_dims) != 4 or not output_dims[1].HasField("dim_value") or output_dims[1].dim_value != 1:
        raise FrozenTrunkHeadError("Detector output must be dynamic NCHW with one channel")


def _nodes_by_name(model: onnx.ModelProto) -> dict[str, onnx.NodeProto]:
    result: dict[str, onnx.NodeProto] = {}
    for node in model.graph.node:
        if node.name:
            if node.name in result:
                raise FrozenTrunkHeadError(f"Duplicate ONNX node name: {node.name}")
            result[node.name] = node
    return result


def _attributes(node: onnx.NodeProto) -> dict[str, object]:
    return {attribute.name: helper.get_attribute_value(attribute) for attribute in node.attribute}


def _expect_node(
    nodes: Mapping[str, onnx.NodeProto],
    name: str,
    op_type: str,
    inputs: Sequence[str],
    outputs: Sequence[str],
    attributes: Mapping[str, object] | None = None,
) -> None:
    node = nodes.get(name)
    if node is None or node.op_type != op_type or tuple(node.input) != tuple(inputs) or tuple(node.output) != tuple(outputs):
        raise FrozenTrunkHeadError(f"Reviewed DB head node changed: {name}")
    if attributes is not None and _attributes(node) != dict(attributes):
        raise FrozenTrunkHeadError(f"Reviewed DB head attributes changed: {name}")


def _validate_head_topology(model: onnx.ModelProto) -> None:
    nodes = _nodes_by_name(model)
    _expect_node(nodes, "Identity.237", "Identity", ["Concat.1"], [FEATURE_OUTPUT_NAME], {})
    _expect_node(
        nodes,
        "Conv.61",
        "Conv",
        [FEATURE_OUTPUT_NAME, "conv2d_159.w_0"],
        ["p2o.pd_op.conv2d.47.0"],
        {"dilations": [1, 1], "kernel_shape": [3, 3], "strides": [1, 1], "group": 1, "pads": [1, 1, 1, 1]},
    )
    _expect_node(
        nodes,
        "BatchNormalization.1",
        "BatchNormalization",
        ["p2o.pd_op.conv2d.47.0", "batch_norm_0.w_0", "batch_norm_0.b_0", "batch_norm_0.w_1", "batch_norm_0.w_2"],
        ["p2o.pd_op.batch_norm_.1.0"],
        {"epsilon": BN_EPSILON, "momentum": BN_MOMENTUM},
    )
    _expect_node(nodes, "Relu.10", "Relu", ["p2o.pd_op.batch_norm_.1.0"], ["p2o.pd_op.relu.10.0"], {})
    _expect_node(nodes, "Identity.238", "Identity", ["p2o.pd_op.relu.10.0"], ["auto.cast.56"], {})
    _expect_node(nodes, "Identity.239", "Identity", ["conv2d_transpose_0.w_0"], ["auto.cast.57"], {})
    _expect_node(
        nodes,
        "ConvTranspose.0",
        "ConvTranspose",
        ["auto.cast.56", "auto.cast.57"],
        ["ConvTranspose.1"],
        {"dilations": [1, 1], "kernel_shape": [2, 2], "strides": [2, 2], "group": 1, "pads": [0, 0, 0, 0]},
    )
    _expect_node(nodes, "Identity.240", "Identity", ["ConvTranspose.1"], ["p2o.pd_op.conv2d_transpose.0.0"], {})
    _expect_node(nodes, "Identity.241", "Identity", ["p2o.pd_op.full_int_array.63.0"], ["auto.cast.58"], {})
    _expect_node(nodes, "Reshape.52", "Reshape", ["conv2d_transpose_0.b_0", "auto.cast.58"], ["p2o.pd_op.reshape.52.0"], {})
    _expect_node(nodes, "Add.230", "Add", ["p2o.pd_op.conv2d_transpose.0.0", "p2o.pd_op.reshape.52.0"], ["Add.231"], {})
    _expect_node(nodes, "Identity.242", "Identity", ["Add.231"], ["p2o.pd_op.add.115.0"], {})
    _expect_node(
        nodes,
        "BatchNormalization.2",
        "BatchNormalization",
        ["p2o.pd_op.add.115.0", "batch_norm_1.w_0", "batch_norm_1.b_0", "batch_norm_1.w_1", "batch_norm_1.w_2"],
        ["p2o.pd_op.batch_norm_.2.0"],
        {"epsilon": BN_EPSILON, "momentum": BN_MOMENTUM},
    )
    _expect_node(nodes, "Relu.11", "Relu", ["p2o.pd_op.batch_norm_.2.0"], ["p2o.pd_op.relu.11.0"], {})
    _expect_node(nodes, "Identity.243", "Identity", ["p2o.pd_op.relu.11.0"], ["auto.cast.59"], {})
    _expect_node(nodes, "Identity.244", "Identity", ["conv2d_transpose_1.w_0"], ["auto.cast.60"], {})
    _expect_node(
        nodes,
        "ConvTranspose.2",
        "ConvTranspose",
        ["auto.cast.59", "auto.cast.60"],
        ["ConvTranspose.3"],
        {"dilations": [1, 1], "kernel_shape": [2, 2], "strides": [2, 2], "group": 1, "pads": [0, 0, 0, 0]},
    )
    _expect_node(nodes, "Identity.245", "Identity", ["ConvTranspose.3"], ["p2o.pd_op.conv2d_transpose.1.0"], {})
    _expect_node(nodes, "Identity.246", "Identity", ["p2o.pd_op.full_int_array.65.0"], ["auto.cast.61"], {})
    _expect_node(nodes, "Reshape.53", "Reshape", ["conv2d_transpose_1.b_0", "auto.cast.61"], ["p2o.pd_op.reshape.53.0"], {})
    _expect_node(nodes, "Add.232", "Add", ["p2o.pd_op.conv2d_transpose.1.0", "p2o.pd_op.reshape.53.0"], ["Add.233"], {})
    _expect_node(nodes, "Identity.247", "Identity", ["Add.233"], ["p2o.pd_op.add.116.0"], {})
    _expect_node(nodes, "Sigmoid.0", "Sigmoid", ["p2o.pd_op.add.116.0"], [MODEL_OUTPUT_NAME], {})

    consumers = [node.name for node in model.graph.node if FEATURE_OUTPUT_NAME in node.input]
    if consumers != ["Conv.61"]:
        raise FrozenTrunkHeadError("Feature boundary has unexpected consumers")


def _constant_arrays(model: onnx.ModelProto) -> dict[str, np.ndarray]:
    constants: dict[str, np.ndarray] = {}
    for node in model.graph.node:
        if node.op_type != "Constant" or len(node.output) != 1:
            continue
        value_attributes = [attribute for attribute in node.attribute if attribute.name == "value"]
        if len(value_attributes) != 1 or value_attributes[0].type != onnx.AttributeProto.TENSOR:
            continue
        output = node.output[0]
        if output in constants:
            raise FrozenTrunkHeadError(f"Duplicate Constant output: {output}")
        constants[output] = np.asarray(numpy_helper.to_array(value_attributes[0].t))
    return constants


def _extract_required_constants(model: onnx.ModelProto) -> dict[str, np.ndarray]:
    constants = _constant_arrays(model)
    required = {**TRAINABLE_CONSTANT_SHAPES, **FROZEN_BATCH_NORM_SHAPES}
    result: dict[str, np.ndarray] = {}
    for name, shape in required.items():
        value = constants.get(name)
        if value is None or value.shape != shape or value.dtype != np.dtype("float32"):
            raise FrozenTrunkHeadError(f"DB head Constant changed: {name}")
        if not np.isfinite(value).all():
            raise FrozenTrunkHeadError(f"DB head Constant is non-finite: {name}")
        result[name] = np.array(value, dtype=np.float32, copy=True, order="C")
    return result


def _identity(name: str, value: np.ndarray, trainable: bool) -> ConstantIdentity:
    contiguous = np.ascontiguousarray(value)
    return ConstantIdentity(
        name=name,
        shape=tuple(int(dimension) for dimension in contiguous.shape),
        dtype=str(contiguous.dtype),
        sha256=sha256(contiguous.tobytes(order="C")).hexdigest(),
        trainable=trainable,
    )


class FrozenDbHead(nn.Module):
    """Exact inference-mode DB head following the reviewed ONNX tail."""

    def __init__(self, constants: Mapping[str, np.ndarray]) -> None:
        super().__init__()
        for parameter_name, constant_name in _PARAMETER_TO_CONSTANT.items():
            setattr(self, parameter_name, nn.Parameter(torch.from_numpy(constants[constant_name]).clone()))
        for buffer_name, constant_name in _BUFFER_TO_CONSTANT.items():
            self.register_buffer(buffer_name, torch.from_numpy(constants[constant_name]).clone(), persistent=True)

    def forward_logits(self, features: Tensor) -> Tensor:
        if features.ndim != 4 or features.shape[1] != FEATURE_CHANNELS:
            raise FrozenTrunkHeadError(
                f"DB head requires NCHW features with {FEATURE_CHANNELS} channels"
            )
        # Torch's float32 CPU accumulators exceed the fixed ONNX parity bound on
        # retained production tensors. Accumulate the reviewed Conv.61 in
        # float64, then restore its float32 ONNX tensor boundary. Parameters and
        # gradients remain attached to the original float32 tensors.
        value = F.conv2d(
            features.to(torch.float64),
            self.conv_weight.to(torch.float64),
            bias=None,
            stride=1,
            padding=1,
        ).to(torch.float32)
        value = F.batch_norm(
            value,
            self.bn0_running_mean,
            self.bn0_running_variance,
            self.bn0_weight,
            self.bn0_bias,
            training=False,
            momentum=BN_MOMENTUM,
            eps=BN_EPSILON,
        )
        value = F.relu(value)
        value = F.conv_transpose2d(value, self.deconv0_weight, bias=None, stride=2)
        value = value + self.deconv0_bias.reshape(1, HEAD_CHANNELS, 1, 1)
        value = F.batch_norm(
            value,
            self.bn1_running_mean,
            self.bn1_running_variance,
            self.bn1_weight,
            self.bn1_bias,
            training=False,
            momentum=BN_MOMENTUM,
            eps=BN_EPSILON,
        )
        value = F.relu(value)
        value = F.conv_transpose2d(value, self.deconv1_weight, bias=None, stride=2)
        return value + self.deconv1_bias.reshape(1, 1, 1, 1)

    def forward(self, features: Tensor) -> Tensor:
        return torch.sigmoid(self.forward_logits(features))

    def trainable_constant_values(self) -> dict[str, np.ndarray]:
        values: dict[str, np.ndarray] = {}
        for parameter_name, constant_name in _PARAMETER_TO_CONSTANT.items():
            tensor = getattr(self, parameter_name)
            values[constant_name] = np.ascontiguousarray(tensor.detach().cpu().numpy(), dtype=np.float32)
        return values

    def frozen_batch_norm_values(self) -> dict[str, np.ndarray]:
        values: dict[str, np.ndarray] = {}
        for buffer_name, constant_name in _BUFFER_TO_CONSTANT.items():
            tensor = getattr(self, buffer_name)
            values[constant_name] = np.ascontiguousarray(tensor.detach().cpu().numpy(), dtype=np.float32)
        return values


def extract_frozen_trunk_head(
    model_path: Path,
    expected_model_sha256: str = REVIEWED_DETECTOR_SHA256,
) -> FrozenTrunkHeadBundle:
    _, model = _load_reviewed_model(model_path, expected_model_sha256)
    constants = _extract_required_constants(model)
    trainable = tuple(
        _identity(name, constants[name], True) for name in TRAINABLE_CONSTANT_SHAPES
    )
    frozen = tuple(
        _identity(name, constants[name], False) for name in FROZEN_BATCH_NORM_SHAPES
    )
    metadata = FrozenTrunkHeadMetadata(
        source_sha256=expected_model_sha256,
        input_name=MODEL_INPUT_NAME,
        feature_output_name=FEATURE_OUTPUT_NAME,
        output_name=MODEL_OUTPUT_NAME,
        opset=ONNX_OPSET,
        ir_version=ONNX_IR_VERSION,
        trainable_parameter_count=sum(int(np.prod(identity.shape)) for identity in trainable),
        trainable_constants=trainable,
        frozen_batch_norm_constants=frozen,
        target_contract=TRAINING_TARGET_CONTRACT,
    )
    return FrozenTrunkHeadBundle(metadata=metadata, head=FrozenDbHead(constants))


def _ancestor_node_indexes(model: onnx.ModelProto, output_name: str) -> set[int]:
    producers: dict[str, int] = {}
    nodes = list(model.graph.node)
    for index, node in enumerate(nodes):
        for output in node.output:
            if output in producers:
                raise FrozenTrunkHeadError(f"Duplicate tensor producer: {output}")
            producers[output] = index
    required: set[int] = set()
    pending = [output_name]
    visited_values: set[str] = set()
    while pending:
        value = pending.pop()
        if value in visited_values:
            continue
        visited_values.add(value)
        producer = producers.get(value)
        if producer is None:
            continue
        required.add(producer)
        pending.extend(nodes[producer].input)
    if not required or MODEL_INPUT_NAME not in visited_values:
        raise FrozenTrunkHeadError("Feature output is not derived from the detector input")
    return required


def write_frozen_trunk_model(
    source_path: Path,
    output_path: Path,
    expected_source_sha256: str = REVIEWED_DETECTOR_SHA256,
) -> str:
    _, source = _load_reviewed_model(source_path, expected_source_sha256)
    model = onnx.ModelProto()
    model.CopyFrom(source)
    required_indexes = _ancestor_node_indexes(model, FEATURE_OUTPUT_NAME)
    kept_nodes = [node for index, node in enumerate(model.graph.node) if index in required_indexes]
    del model.graph.node[:]
    model.graph.node.extend(kept_nodes)
    del model.graph.output[:]
    model.graph.output.extend(
        [
            helper.make_tensor_value_info(
                FEATURE_OUTPUT_NAME,
                TensorProto.FLOAT,
                ["N", FEATURE_CHANNELS, "H4", "W4"],
            )
        ]
    )
    onnx.checker.check_model(model, full_check=True)
    payload = model.SerializeToString()
    output_path.parent.mkdir(parents=True, exist_ok=True)
    with output_path.open("xb") as stream:
        stream.write(payload)
    return sha256(payload).hexdigest()


def _replace_constant(model: onnx.ModelProto, name: str, value: np.ndarray) -> None:
    matches = [node for node in model.graph.node if node.op_type == "Constant" and tuple(node.output) == (name,)]
    if len(matches) != 1:
        raise FrozenTrunkHeadError(f"Expected exactly one Constant producer for {name}")
    attributes = [attribute for attribute in matches[0].attribute if attribute.name == "value"]
    if len(attributes) != 1 or attributes[0].type != onnx.AttributeProto.TENSOR:
        raise FrozenTrunkHeadError(f"Constant tensor encoding changed: {name}")
    tensor_name = attributes[0].t.name
    attributes[0].t.CopyFrom(numpy_helper.from_array(value, name=tensor_name))


def _semantic_constant_hashes(model: onnx.ModelProto) -> dict[str, str]:
    return {
        name: sha256(np.ascontiguousarray(value).tobytes(order="C")).hexdigest()
        for name, value in _constant_arrays(model).items()
    }


def patch_head_constants(
    source_path: Path,
    output_path: Path,
    head: FrozenDbHead,
    expected_source_sha256: str = REVIEWED_DETECTOR_SHA256,
) -> PatchResult:
    source_bytes, source = _load_reviewed_model(source_path, expected_source_sha256)
    original = _extract_required_constants(source)
    frozen = head.frozen_batch_norm_values()
    for name, shape in FROZEN_BATCH_NORM_SHAPES.items():
        value = frozen.get(name)
        if value is None or value.shape != shape or value.dtype != np.dtype("float32") or not np.array_equal(value, original[name]):
            raise FrozenTrunkHeadError(f"Frozen batch-normalization state changed: {name}")

    replacements = head.trainable_constant_values()
    if set(replacements) != set(TRAINABLE_CONSTANT_SHAPES):
        raise FrozenTrunkHeadError("Trainable DB head Constant inventory changed")
    changed: list[str] = []
    unchanged: list[str] = []
    for name, shape in TRAINABLE_CONSTANT_SHAPES.items():
        value = replacements[name]
        if value.shape != shape or value.dtype != np.dtype("float32") or not np.isfinite(value).all():
            raise FrozenTrunkHeadError(f"Invalid trainable DB head Constant: {name}")
        (changed if not np.array_equal(value, original[name]) else unchanged).append(name)

    output_path.parent.mkdir(parents=True, exist_ok=True)
    if not changed:
        with output_path.open("xb") as stream:
            stream.write(source_bytes)
        return PatchResult(
            source_sha256=expected_source_sha256,
            output_sha256=expected_source_sha256,
            changed_constants=(),
            unchanged_trainable_constants=tuple(unchanged),
        )

    patched = onnx.ModelProto()
    patched.CopyFrom(source)
    before_hashes = _semantic_constant_hashes(source)
    for name in changed:
        _replace_constant(patched, name, replacements[name])
    onnx.checker.check_model(patched, full_check=True)
    after_hashes = _semantic_constant_hashes(patched)
    observed_changed = {name for name in before_hashes if before_hashes[name] != after_hashes.get(name)}
    if observed_changed != set(changed) or set(before_hashes) != set(after_hashes):
        raise FrozenTrunkHeadError("ONNX patch changed a non-allowlisted Constant")
    restored = onnx.ModelProto()
    restored.CopyFrom(patched)
    for name in changed:
        _replace_constant(restored, name, original[name])
    if restored.SerializeToString() != source_bytes:
        raise FrozenTrunkHeadError("ONNX patch changed graph content outside the allowlisted values")
    _validate_io(patched)
    _validate_head_topology(patched)
    _extract_required_constants(patched)
    payload = patched.SerializeToString()
    with output_path.open("xb") as stream:
        stream.write(payload)
    return PatchResult(
        source_sha256=expected_source_sha256,
        output_sha256=sha256(payload).hexdigest(),
        changed_constants=tuple(changed),
        unchanged_trainable_constants=tuple(unchanged),
    )


def deterministic_production_tensor(shape: tuple[int, int, int, int], seed: int) -> np.ndarray:
    if (
        len(shape) != 4
        or shape[0] < 1
        or shape[1] != 3
        or shape[2] < 1
        or shape[3] < 1
        or shape[2] % 32 != 0
        or shape[3] % 32 != 0
        or isinstance(seed, bool)
        or not isinstance(seed, int)
    ):
        raise FrozenTrunkHeadError("Production parity tensor shape or seed is invalid")
    generator = np.random.default_rng(seed)
    batch, _, height, width = shape
    bgr = generator.integers(0, 256, size=(batch, height, width, 3), dtype=np.uint8)
    means = np.asarray([0.485, 0.456, 0.406], dtype=np.float32).reshape(1, 1, 1, 3)
    scales = (
        np.float32(1) / np.asarray([0.229, 0.224, 0.225], dtype=np.float32)
    ).reshape(1, 1, 1, 3)
    normalized = (bgr.astype(np.float32) / np.float32(255.0) - means) * scales
    return np.ascontiguousarray(normalized.transpose(0, 3, 1, 2))


def run_step_zero_parity(
    source_path: Path,
    input_shapes: Iterable[tuple[int, int, int, int]],
    *,
    expected_source_sha256: str = REVIEWED_DETECTOR_SHA256,
    seed: int = 20260909,
    tolerance: float = 1e-5,
) -> StepZeroParityResult:
    if not np.isfinite(tolerance) or tolerance <= 0:
        raise FrozenTrunkHeadError("Step-zero parity tolerance must be finite and positive")
    shapes = tuple(input_shapes)
    if not shapes:
        raise FrozenTrunkHeadError("Step-zero parity requires at least one input shape")
    bundle = extract_frozen_trunk_head(source_path, expected_source_sha256)
    try:
        import onnxruntime as ort
    except ImportError as error:
        raise FrozenTrunkHeadError("ONNX Runtime is required for step-zero parity") from error

    with tempfile.TemporaryDirectory(prefix="graphreader-frozen-trunk-") as temporary:
        feature_path = Path(temporary) / "frozen-trunk.onnx"
        feature_sha256 = write_frozen_trunk_model(source_path, feature_path, expected_source_sha256)
        providers = ["CPUExecutionProvider"]
        full_session = ort.InferenceSession(str(source_path), providers=providers)
        feature_session = ort.InferenceSession(str(feature_path), providers=providers)
        if full_session.get_providers() != providers or feature_session.get_providers() != providers:
            raise FrozenTrunkHeadError("Step-zero parity requires CPUExecutionProvider only")
        errors: list[tuple[tuple[int, int, int, int], float]] = []
        bundle.head.eval()
        with torch.inference_mode(), torch.backends.mkldnn.flags(enabled=False):
            for index, shape in enumerate(shapes):
                values = deterministic_production_tensor(shape, seed + index)
                expected = np.asarray(
                    full_session.run([MODEL_OUTPUT_NAME], {MODEL_INPUT_NAME: values})[0],
                    dtype=np.float32,
                )
                features = np.asarray(
                    feature_session.run([FEATURE_OUTPUT_NAME], {MODEL_INPUT_NAME: values})[0],
                    dtype=np.float32,
                )
                actual = bundle.head(torch.from_numpy(np.ascontiguousarray(features))).cpu().numpy()
                if expected.shape != actual.shape or not np.isfinite(expected).all() or not np.isfinite(actual).all():
                    raise FrozenTrunkHeadError(f"Step-zero output shape or values changed for {shape}")
                errors.append((shape, float(np.max(np.abs(expected - actual), initial=0.0))))
    maximum = max(error for _, error in errors)
    return StepZeroParityResult(
        source_sha256=expected_source_sha256,
        feature_model_sha256=feature_sha256,
        provider="CPUExecutionProvider",
        torch_backend="cpu-mkldnn-disabled-float64-conv0",
        errors_by_shape=tuple(errors),
        maximum_absolute_error=maximum,
        tolerance=tolerance,
        passed=maximum <= tolerance,
    )
