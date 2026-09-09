# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang

from hashlib import sha256
from pathlib import Path

import numpy as np
import onnx
from onnx import TensorProto, helper, numpy_helper
import pytest
import torch

from ml.ocr.official_bakeoff import frozen_trunk_head as subject


def _constant(name: str, value: np.ndarray) -> onnx.NodeProto:
    return helper.make_node("Constant", [], [name], value=numpy_helper.from_array(value, name=name))


def _fixture_model(path: Path, *, mutate_topology: bool = False) -> str:
    rng = np.random.default_rng(20260909)
    values: dict[str, np.ndarray] = {
        "trunk.w_0": rng.normal(0, 0.03, (96, 3, 4, 4)).astype(np.float32),
        "conv2d_159.w_0": rng.normal(0, 0.03, (24, 96, 3, 3)).astype(np.float32),
        "batch_norm_0.w_0": rng.normal(1, 0.03, 24).astype(np.float32),
        "batch_norm_0.b_0": rng.normal(0, 0.03, 24).astype(np.float32),
        "batch_norm_0.w_1": rng.normal(0, 0.03, 24).astype(np.float32),
        "batch_norm_0.w_2": rng.uniform(0.5, 1.5, 24).astype(np.float32),
        "conv2d_transpose_0.w_0": rng.normal(0, 0.03, (24, 24, 2, 2)).astype(np.float32),
        "conv2d_transpose_0.b_0": rng.normal(0, 0.03, 24).astype(np.float32),
        "batch_norm_1.w_0": rng.normal(1, 0.03, 24).astype(np.float32),
        "batch_norm_1.b_0": rng.normal(0, 0.03, 24).astype(np.float32),
        "batch_norm_1.w_1": rng.normal(0, 0.03, 24).astype(np.float32),
        "batch_norm_1.w_2": rng.uniform(0.5, 1.5, 24).astype(np.float32),
        "conv2d_transpose_1.w_0": rng.normal(0, 0.03, (24, 1, 2, 2)).astype(np.float32),
        "conv2d_transpose_1.b_0": rng.normal(0, 0.03, 1).astype(np.float32),
        "p2o.pd_op.full_int_array.63.0": np.asarray([1, 24, 1, 1], dtype=np.int64),
        "p2o.pd_op.full_int_array.65.0": np.asarray([1, 1, 1, 1], dtype=np.int64),
    }
    nodes = [_constant(name, value) for name, value in values.items()]
    nodes.extend(
        [
            helper.make_node("Conv", ["x", "trunk.w_0"], ["Concat.1"], name="Conv.0", kernel_shape=[4, 4], strides=[4, 4]),
            helper.make_node("Identity", ["Concat.1"], [subject.FEATURE_OUTPUT_NAME], name="Identity.237"),
            helper.make_node("Conv", [subject.FEATURE_OUTPUT_NAME, "conv2d_159.w_0"], ["p2o.pd_op.conv2d.47.0"], name="Conv.61", dilations=[1, 1], kernel_shape=[3, 3], strides=[1, 1], group=1, pads=[1, 1, 1, 1]),
            helper.make_node("BatchNormalization", ["p2o.pd_op.conv2d.47.0", "batch_norm_0.w_0", "batch_norm_0.b_0", "batch_norm_0.w_1", "batch_norm_0.w_2"], ["p2o.pd_op.batch_norm_.1.0"], name="BatchNormalization.1", epsilon=subject.BN_EPSILON, momentum=subject.BN_MOMENTUM),
            helper.make_node("Relu", ["p2o.pd_op.batch_norm_.1.0"], ["p2o.pd_op.relu.10.0"], name="Relu.10"),
            helper.make_node("Identity", ["p2o.pd_op.relu.10.0"], ["auto.cast.56"], name="Identity.238"),
            helper.make_node("Identity", ["conv2d_transpose_0.w_0"], ["auto.cast.57"], name="Identity.239"),
            helper.make_node("ConvTranspose", ["auto.cast.56", "auto.cast.57"], ["ConvTranspose.1"], name="ConvTranspose.0", dilations=[1, 1], kernel_shape=[2, 2], strides=[2, 2], group=1, pads=[0, 0, 0, 0]),
            helper.make_node("Identity", ["ConvTranspose.1"], ["p2o.pd_op.conv2d_transpose.0.0"], name="Identity.240"),
            helper.make_node("Identity", ["p2o.pd_op.full_int_array.63.0"], ["auto.cast.58"], name="Identity.241"),
            helper.make_node("Reshape", ["conv2d_transpose_0.b_0", "auto.cast.58"], ["p2o.pd_op.reshape.52.0"], name="Reshape.52"),
            helper.make_node("Add", ["p2o.pd_op.conv2d_transpose.0.0", "p2o.pd_op.reshape.52.0"], ["Add.231"], name="Add.230"),
            helper.make_node("Identity", ["Add.231"], ["p2o.pd_op.add.115.0"], name="Identity.242"),
            helper.make_node("BatchNormalization", ["p2o.pd_op.add.115.0", "batch_norm_1.w_0", "batch_norm_1.b_0", "batch_norm_1.w_1", "batch_norm_1.w_2"], ["p2o.pd_op.batch_norm_.2.0"], name="BatchNormalization.2", epsilon=subject.BN_EPSILON, momentum=subject.BN_MOMENTUM),
            helper.make_node("Relu", ["p2o.pd_op.batch_norm_.2.0"], ["p2o.pd_op.relu.11.0"], name="Relu.11"),
            helper.make_node("Identity", ["p2o.pd_op.relu.11.0"], ["auto.cast.59"], name="Identity.243"),
            helper.make_node("Identity", ["conv2d_transpose_1.w_0"], ["auto.cast.60"], name="Identity.244"),
            helper.make_node("ConvTranspose", ["auto.cast.59", "auto.cast.60"], ["ConvTranspose.3"], name="ConvTranspose.2", dilations=[1, 1], kernel_shape=[2, 2], strides=[2, 2], group=1, pads=[0, 0, 0, 0]),
            helper.make_node("Identity", ["ConvTranspose.3"], ["p2o.pd_op.conv2d_transpose.1.0"], name="Identity.245"),
            helper.make_node("Identity", ["p2o.pd_op.full_int_array.65.0"], ["auto.cast.61"], name="Identity.246"),
            helper.make_node("Reshape", ["conv2d_transpose_1.b_0", "auto.cast.61"], ["p2o.pd_op.reshape.53.0"], name="Reshape.53"),
            helper.make_node("Add", ["p2o.pd_op.conv2d_transpose.1.0", "p2o.pd_op.reshape.53.0"], ["Add.233"], name="Add.232"),
            helper.make_node("Identity", ["Add.233"], ["p2o.pd_op.add.116.0"], name="Identity.247"),
            helper.make_node("Sigmoid", ["p2o.pd_op.add.116.0"], [subject.MODEL_OUTPUT_NAME], name="Sigmoid.0"),
        ]
    )
    if mutate_topology:
        next(node for node in nodes if node.name == "Conv.61").attribute[2].ints[:] = [2, 2]
    graph = helper.make_graph(
        nodes,
        "fixture",
        [helper.make_tensor_value_info(subject.MODEL_INPUT_NAME, TensorProto.FLOAT, ["N", 3, "H", "W"])],
        [helper.make_tensor_value_info(subject.MODEL_OUTPUT_NAME, TensorProto.FLOAT, ["N", 1, "H", "W"])],
    )
    model = helper.make_model(graph, opset_imports=[helper.make_opsetid("", subject.ONNX_OPSET)])
    model.ir_version = subject.ONNX_IR_VERSION
    onnx.checker.check_model(model, full_check=True)
    payload = model.SerializeToString()
    path.write_bytes(payload)
    return sha256(payload).hexdigest()


def test_extracts_exact_trainable_head_and_frozen_batch_norm(tmp_path: Path) -> None:
    model_path = tmp_path / "model.onnx"
    identity = _fixture_model(model_path)

    bundle = subject.extract_frozen_trunk_head(model_path, identity)

    assert bundle.metadata.trainable_parameter_count == 23_257
    assert tuple(item.name for item in bundle.metadata.trainable_constants) == tuple(subject.TRAINABLE_CONSTANT_SHAPES)
    assert tuple(item.name for item in bundle.metadata.frozen_batch_norm_constants) == tuple(subject.FROZEN_BATCH_NORM_SHAPES)
    assert all(parameter.requires_grad for parameter in bundle.head.parameters())
    assert not any(buffer.requires_grad for buffer in bundle.head.buffers())
    assert "shrink-map" in bundle.metadata.target_contract
    assert "filled truth-box targets are incompatible" in bundle.metadata.target_contract


def test_head_accumulates_first_convolution_in_float64_then_restores_float32(
    tmp_path: Path, monkeypatch
) -> None:
    model_path = tmp_path / "model.onnx"
    identity = _fixture_model(model_path)
    head = subject.extract_frozen_trunk_head(model_path, identity).head
    observed: list[tuple[torch.dtype, torch.dtype]] = []
    original = subject.F.conv2d

    def inspect(input_tensor, weight, *args, **kwargs):
        observed.append((input_tensor.dtype, weight.dtype))
        return original(input_tensor, weight, *args, **kwargs)

    monkeypatch.setattr(subject.F, "conv2d", inspect)
    output = head.forward_logits(torch.zeros((1, 96, 1, 1), dtype=torch.float32))

    assert observed == [(torch.float64, torch.float64)]
    assert output.dtype == torch.float32
    assert all(parameter.dtype == torch.float32 for parameter in head.parameters())


def test_rejects_changed_head_topology(tmp_path: Path) -> None:
    model_path = tmp_path / "changed.onnx"
    identity = _fixture_model(model_path, mutate_topology=True)

    with pytest.raises(subject.FrozenTrunkHeadError, match="Conv.61"):
        subject.extract_frozen_trunk_head(model_path, identity)


def test_writes_ancestor_only_frozen_trunk(tmp_path: Path) -> None:
    source = tmp_path / "source.onnx"
    source_identity = _fixture_model(source)
    output = tmp_path / "trunk.onnx"

    output_identity = subject.write_frozen_trunk_model(source, output, source_identity)
    model = onnx.load(output)
    onnx.checker.check_model(model, full_check=True)

    assert output_identity == subject.sha256_file(output)
    assert [item.name for item in model.graph.output] == [subject.FEATURE_OUTPUT_NAME]
    assert "Conv.0" in {node.name for node in model.graph.node}
    assert "Conv.61" not in {node.name for node in model.graph.node}
    assert "conv2d_159.w_0" not in {value for node in model.graph.node for value in node.output}


def test_no_op_patch_is_byte_identical(tmp_path: Path) -> None:
    source = tmp_path / "source.onnx"
    source_identity = _fixture_model(source)
    output = tmp_path / "copy.onnx"
    head = subject.extract_frozen_trunk_head(source, source_identity).head

    result = subject.patch_head_constants(source, output, head, source_identity)

    assert output.read_bytes() == source.read_bytes()
    assert result.output_sha256 == source_identity
    assert result.changed_constants == ()
    assert set(result.unchanged_trainable_constants) == set(subject.TRAINABLE_CONSTANT_SHAPES)


def test_patch_changes_only_allowlisted_trainable_constants(tmp_path: Path) -> None:
    source = tmp_path / "source.onnx"
    source_identity = _fixture_model(source)
    output = tmp_path / "updated.onnx"
    head = subject.extract_frozen_trunk_head(source, source_identity).head
    with torch.no_grad():
        head.deconv1_bias.add_(0.125)

    result = subject.patch_head_constants(source, output, head, source_identity)
    before = subject._semantic_constant_hashes(onnx.load(source))
    after = subject._semantic_constant_hashes(onnx.load(output))

    assert result.changed_constants == ("conv2d_transpose_1.b_0",)
    assert {name for name in before if before[name] != after[name]} == {"conv2d_transpose_1.b_0"}
    onnx.checker.check_model(onnx.load(output), full_check=True)


def test_patch_rejects_changed_frozen_batch_norm_state(tmp_path: Path) -> None:
    source = tmp_path / "source.onnx"
    source_identity = _fixture_model(source)
    head = subject.extract_frozen_trunk_head(source, source_identity).head
    head.bn0_running_mean.add_(1)

    with pytest.raises(subject.FrozenTrunkHeadError, match="Frozen batch-normalization"):
        subject.patch_head_constants(source, tmp_path / "invalid.onnx", head, source_identity)


def test_step_zero_cpu_parity_uses_production_normalized_inputs(tmp_path: Path, monkeypatch) -> None:
    source = tmp_path / "source.onnx"
    source_identity = _fixture_model(source)
    observed_backends: list[bool] = []
    original_forward = subject.FrozenDbHead.forward

    def observed_forward(self, features):
        observed_backends.append(torch.backends.mkldnn.enabled)
        return original_forward(self, features)

    monkeypatch.setattr(subject.FrozenDbHead, "forward", observed_forward)

    with torch.backends.mkldnn.flags(enabled=True):
        result = subject.run_step_zero_parity(
            source,
            [(1, 3, 128, 128), (2, 3, 128, 128)],
            expected_source_sha256=source_identity,
            tolerance=1e-5,
        )
        assert torch.backends.mkldnn.enabled is True

    assert result.provider == "CPUExecutionProvider"
    assert result.torch_backend == "cpu-mkldnn-disabled-float64-conv0"
    assert observed_backends and not any(observed_backends)
    assert result.passed
    assert result.maximum_absolute_error <= 1e-5
    first = subject.deterministic_production_tensor((1, 3, 128, 128), 7)
    second = subject.deterministic_production_tensor((1, 3, 128, 128), 7)
    assert first.dtype == np.float32
    assert first.flags.c_contiguous
    np.testing.assert_array_equal(first, second)
    bgr = np.random.default_rng(7).integers(0, 256, size=(1, 128, 128, 3), dtype=np.uint8)
    expected_blue = (
        np.float32(bgr[0, 0, 0, 0]) / np.float32(255) - np.float32(0.485)
    ) * (np.float32(1) / np.float32(0.229))
    assert first[0, 0, 0, 0] == expected_blue
    production_bounded = subject.deterministic_production_tensor((1, 3, 960, 32), 11)
    assert production_bounded.shape == (1, 3, 960, 32)
    with pytest.raises(subject.FrozenTrunkHeadError, match="shape or seed"):
        subject.deterministic_production_tensor((1, 3, 31, 128), 7)


def test_step_zero_restores_mkldnn_after_torch_failure(tmp_path: Path, monkeypatch) -> None:
    source = tmp_path / "source.onnx"
    source_identity = _fixture_model(source)

    def fail_forward(self, features):
        assert torch.backends.mkldnn.enabled is False
        raise RuntimeError("injected head failure")

    monkeypatch.setattr(subject.FrozenDbHead, "forward", fail_forward)
    with torch.backends.mkldnn.flags(enabled=True):
        with pytest.raises(RuntimeError, match="injected head failure"):
            subject.run_step_zero_parity(
                source,
                [(1, 3, 128, 128)],
                expected_source_sha256=source_identity,
            )
        assert torch.backends.mkldnn.enabled is True


def test_rejects_existing_output_and_wrong_source_identity(tmp_path: Path) -> None:
    source = tmp_path / "source.onnx"
    source_identity = _fixture_model(source)
    existing = tmp_path / "existing.onnx"
    existing.write_bytes(b"owned")

    with pytest.raises(FileExistsError):
        subject.write_frozen_trunk_model(source, existing, source_identity)
    with pytest.raises(subject.FrozenTrunkHeadError, match="SHA-256 changed"):
        subject.extract_frozen_trunk_head(source, "0" * 64)
