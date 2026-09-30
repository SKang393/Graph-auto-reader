# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Inspect and isolate the reviewed server CTC head without authorizing training.

The exact approved ONNX graph remains the deployment artifact. Only its two
final linear-layer Constants can be replaced, with every token class preserved.
No model download, optimizer, dataset access or production approval occurs here.
"""
from __future__ import annotations

from hashlib import sha256
from pathlib import Path

import numpy as np
import onnx
from onnx import TensorProto, helper, numpy_helper
import torch
from torch import nn

MODEL_SHA256 = "0980049401eceb506952f769765a6b95be07cb1197dcbb13c93f107a328f8418"
INPUT = "x"
OUTPUT = "fetch_name_0"
FEATURE = "p2o.pd_op.transpose.8.0"
FEATURES = 120
CLASSES = 18385
WEIGHT = "linear_9.w_0"
BIAS = "linear_9.b_0"
SHAPES = {WEIGHT: (FEATURES, CLASSES), BIAS: (CLASSES,)}


def _require(condition: bool, message: str) -> None:
    if not condition:
        raise ValueError(message)


def _producers(model: onnx.ModelProto) -> dict[str, onnx.NodeProto]:
    result = {}
    for node in model.graph.node:
        for output in node.output:
            _require(output not in result, "Duplicate tensor producer")
            result[output] = node
    return result


def _constant(node: onnx.NodeProto, shape: tuple[int, ...]) -> np.ndarray:
    _require(node.op_type == "Constant" and not node.input, "Head value is not a Constant")
    _require(len(node.attribute) == 1 and node.attribute[0].name == "value" and
             node.attribute[0].type == onnx.AttributeProto.TENSOR,
             "Head Constant encoding changed")
    value = numpy_helper.to_array(node.attribute[0].t)
    _require(value.dtype == np.float32 and value.shape == shape and
             np.isfinite(value).all(), "Head Constant shape, type or values changed")
    return value


def _validate(model: onnx.ModelProto) -> dict[str, np.ndarray]:
    _require(model.ir_version == 6 and
             [(item.domain, item.version) for item in model.opset_import] == [("", 11)],
             "Reviewed ONNX format changed")
    _require(not model.graph.initializer and len(model.graph.input) == 1 and
             len(model.graph.output) == 1, "Reviewed model interface changed")
    for item, name, dimensions in ((model.graph.input[0], INPUT, (0, 3, 48, 0)),
                                   (model.graph.output[0], OUTPUT, (0, 0, CLASSES))):
        tensor = item.type.tensor_type
        _require(item.name == name and tensor.elem_type == TensorProto.FLOAT and
                 tuple(d.dim_value for d in tensor.shape.dim) == dimensions,
                 "Reviewed tensor contract changed")
    producers = _producers(model)
    topology = (
        (FEATURE, "Transpose", ("p2o.pd_op.squeeze.0.0",), {"perm": [0, 2, 1]}),
        ("p2o.pd_op.matmul.12.0", "MatMul", (FEATURE, WEIGHT), {}),
        ("Add.51", "Add", ("p2o.pd_op.matmul.12.0", BIAS), {}),
        ("p2o.pd_op.add.14.0", "Identity", ("Add.51",), {}),
        (OUTPUT, "Softmax", ("p2o.pd_op.add.14.0",), {"axis": 2}),
    )
    for output, operation, inputs, attributes in topology:
        node = producers.get(output)
        _require(node is not None and node.op_type == operation and
                 tuple(node.input) == inputs and tuple(node.output) == (output,) and
                 {a.name: helper.get_attribute_value(a) for a in node.attribute} == attributes,
                 "Reviewed CTC head topology changed")
    _require(set(SHAPES) <= producers.keys(), "Head Constant missing")
    return {name: _constant(producers[name], shape) for name, shape in SHAPES.items()}


def load_reviewed(path: Path) -> onnx.ModelProto:
    payload = path.read_bytes()
    _require(sha256(payload).hexdigest() == MODEL_SHA256, "Unreviewed recognizer payload")
    model = onnx.load_from_string(payload)
    _validate(model)
    return model


class RecognitionHead(nn.Module):
    """Return logits for every original CTC class, including blank at index zero."""

    def __init__(self, model: onnx.ModelProto):
        super().__init__()
        constants = _validate(model)
        self.weight = nn.Parameter(torch.from_numpy(constants[WEIGHT].copy()))
        self.bias = nn.Parameter(torch.from_numpy(constants[BIAS].copy()))

    def forward(self, features: torch.Tensor) -> torch.Tensor:
        if features.ndim != 3 or features.shape[-1] != FEATURES:
            raise ValueError("CTC features must be batch, time, 120")
        return features @ self.weight + self.bias


def write_trunk(source: Path, destination: Path) -> dict:
    model = load_reviewed(source)
    producers = _producers(model)
    visited, pending = set(), [FEATURE]
    while pending:
        value = pending.pop()
        if value in visited:
            continue
        visited.add(value)
        node = producers.get(value)
        if node is not None:
            pending.extend(node.input)
    _require(INPUT in visited and WEIGHT not in visited and BIAS not in visited,
             "Feature boundary does not isolate the final linear head")
    kept = [n for n in model.graph.node if any(output in visited for output in n.output)]
    del model.graph.node[:]
    model.graph.node.extend(kept)
    del model.graph.output[:]
    model.graph.output.append(helper.make_tensor_value_info(
        FEATURE, TensorProto.FLOAT, ["N", "T", FEATURES]))
    onnx.checker.check_model(model, full_check=True)
    payload = model.SerializeToString()
    with destination.open("xb") as stream:
        stream.write(payload)
    return {"sha256": sha256(payload).hexdigest(), "nodes": len(kept),
            "feature_output": FEATURE, "feature_channels": FEATURES}


def patch_head(source: Path, destination: Path, head: RecognitionHead) -> dict:
    model = load_reviewed(source)
    original = model.SerializeToString()
    producers = _producers(model)
    values = {WEIGHT: head.weight.detach().cpu().numpy(),
              BIAS: head.bias.detach().cpu().numpy()}
    changed = []
    saved = {}
    for name, shape in SHAPES.items():
        value = values[name]
        _require(value.dtype == np.float32 and value.shape == shape and
                 np.isfinite(value).all(), "Invalid replacement CTC head")
        node = producers[name]
        if np.array_equal(value, _constant(node, shape)):
            continue
        saved[name] = node.SerializeToString()
        tensor = node.attribute[0].t
        tensor.CopyFrom(numpy_helper.from_array(np.ascontiguousarray(value), name=tensor.name))
        changed.append(name)
    _validate(model)
    onnx.checker.check_model(model, full_check=True)
    payload = model.SerializeToString() if changed else source.read_bytes()
    for name, value in saved.items():
        producers[name].ParseFromString(value)
    _require(model.SerializeToString() == original, "Patch changed frozen graph content")
    with destination.open("xb") as stream:
        stream.write(payload)
    return {"source_sha256": MODEL_SHA256, "sha256": sha256(payload).hexdigest(),
            "changed_constants": changed, "all_other_graph_content_unchanged": True,
            "class_count": CLASSES}


def validate_tokens(tokens: list[str], alphabet: str) -> tuple[str, ...]:
    _require(len(tokens) == CLASSES - 1 and
             all(isinstance(t, str) and t and not any(0xD800 <= ord(c) <= 0xDFFF for c in t)
                 for t in tokens) and len(set(tokens)) == len(tokens) and "".join(tokens) == alphabet,
             "Exact reviewed token inventory must be preserved")
    return tuple(tokens)


def encode_text(text: str, tokens: tuple[str, ...]) -> tuple[int, ...]:
    """Encode literal text without case, number, whitespace or Unicode correction."""
    _require(bool(text), "Empty CTC target")
    by_first: dict[str, list[tuple[str, int]]] = {}
    for index, token in enumerate(tokens, 1):
        _require(bool(token), "Empty CTC token")
        by_first.setdefault(token[0], []).append((token, index))
    ways: list[list[tuple[int, ...]]] = [[] for _ in range(len(text) + 1)]
    ways[-1] = [()]
    for start in range(len(text) - 1, -1, -1):
        for token, index in by_first.get(text[start], []):
            if text.startswith(token, start):
                ways[start].extend((index, *tail) for tail in ways[start + len(token)])
                ways[start] = ways[start][:2]
    _require(len(ways[0]) == 1, "Target is unsupported or has ambiguous token segmentation")
    return ways[0][0]


def decode_classes(classes: list[int], tokens: tuple[str, ...]) -> str:
    previous, result = -1, []
    for index in classes:
        _require(0 <= index <= len(tokens), "CTC class outside the token inventory")
        if index and index != previous:
            result.append(tokens[index - 1])
        previous = index
    return "".join(result)
