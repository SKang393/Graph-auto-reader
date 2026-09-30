# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""No external weights, inference, dataset access or optimizer in these tests."""
import numpy as np
import onnx
from onnx import TensorProto, helper, numpy_helper
import pytest
import torch

from ml.ocr.official_bakeoff import recognition_head as target


@pytest.fixture(scope="module")
def model():
    nodes = [helper.make_node("ReduceMean", [target.INPUT], ["column_mean"], axes=[2], keepdims=0),
             helper.make_node("Constant", [], ["tile_count"],
                 value=numpy_helper.from_array(np.array([1, 40, 1], dtype=np.int64))),
             helper.make_node("Tile", ["column_mean", "tile_count"], ["p2o.pd_op.squeeze.0.0"]),
             helper.make_node("Transpose", ["p2o.pd_op.squeeze.0.0"], [target.FEATURE], perm=[0, 2, 1])]
    for name, shape in target.SHAPES.items():
        nodes.append(helper.make_node("Constant", [], [name],
            value=numpy_helper.from_array(np.zeros(shape, dtype=np.float32))))
    nodes.extend([
        helper.make_node("MatMul", [target.FEATURE, target.WEIGHT], ["p2o.pd_op.matmul.12.0"]),
        helper.make_node("Add", ["p2o.pd_op.matmul.12.0", target.BIAS], ["Add.51"]),
        helper.make_node("Identity", ["Add.51"], ["p2o.pd_op.add.14.0"]),
        helper.make_node("Softmax", ["p2o.pd_op.add.14.0"], [target.OUTPUT], axis=2)])
    graph = helper.make_graph(nodes, "head-structure-fixture",
        [helper.make_tensor_value_info(target.INPUT, TensorProto.FLOAT, ["N", 3, 48, "W"])],
        [helper.make_tensor_value_info(target.OUTPUT, TensorProto.FLOAT, ["N", "T", target.CLASSES])])
    return helper.make_model(graph, ir_version=6, opset_imports=[helper.make_opsetid("", 11)])


def clone(model):
    return onnx.load_from_string(model.SerializeToString())


def test_head_keeps_every_class_and_uses_all_features(model):
    head = target.RecognitionHead(model)
    with torch.no_grad():
        head.weight[119, 18384] = 3
        head.bias[0] = 2
        result = head(torch.ones(1, 2, 120))
    assert result.shape == (1, 2, 18385)
    assert result[0, 0, 0] == 2 and result[0, 0, 18384] == 3


@pytest.mark.parametrize("shape", [(120,), (2, 120), (1, 3, 119)])
def test_invalid_feature_contract_fails(model, shape):
    with pytest.raises(ValueError, match="features"):
        target.RecognitionHead(model)(torch.zeros(shape))


@pytest.mark.parametrize("change", ["class_count", "axis", "weight_shape", "duplicate", "nonfinite"])
def test_changed_graph_contract_fails(model, change):
    altered = clone(model)
    if change == "class_count":
        altered.graph.output[0].type.tensor_type.shape.dim[-1].dim_value -= 1
    elif change == "axis":
        altered.graph.node[-1].attribute[0].i = 1
    elif change == "duplicate":
        altered.graph.node.append(altered.graph.node[-1])
    else:
        producer = target._producers(altered)[target.WEIGHT]
        values = np.zeros(target.SHAPES[target.WEIGHT], np.float32)
        if change == "weight_shape":
            values = values[:-1]
        else:
            values[0, 0] = np.nan
        producer.attribute[0].t.CopyFrom(numpy_helper.from_array(values))
    with pytest.raises(ValueError):
        target.RecognitionHead(altered)


def test_unreviewed_graph_rejected_before_parsing(tmp_path):
    source = tmp_path / "model.onnx"
    source.write_bytes(b"unreviewed")
    with pytest.raises(ValueError, match="Unreviewed"):
        target.load_reviewed(source)


def test_literal_targets_preserve_ambiguous_digits_case_and_spaces():
    tokens = tuple("PO01articpn ")
    text = "Participant O1"
    encoded = target.encode_text(text, tokens)
    assert target.decode_classes([value for index in encoded for value in (index, 0)], tokens) == text
    assert target.encode_text("O1", tokens) != target.encode_text("01", tokens)


def test_repeated_symbols_need_a_blank_to_remain_repeated():
    assert target.decode_classes([1, 1, 0, 1], ("a",)) == "aa"
    assert target.decode_classes([1, 1], ("a",)) == "a"


def test_multi_scalar_and_non_bmp_tokens_are_whole_classes():
    tokens = ("A", "\U0001f1e9\U0001f1ea", "\U00020000")
    assert target.encode_text("A\U0001f1e9\U0001f1ea\U00020000", tokens) == (1, 2, 3)
    assert target.decode_classes([1, 2, 3], tokens) == "".join(tokens)


@pytest.mark.parametrize("text,tokens", [("", ("A",)), ("O", ("0",)), ("ab", ("a", "b", "ab"))])
def test_unrepresentable_or_ambiguous_target_rejected(text, tokens):
    with pytest.raises(ValueError):
        target.encode_text(text, tokens)


@pytest.mark.parametrize("classes", [[-1], [2]])
def test_out_of_range_decoder_class_rejected(classes):
    with pytest.raises(ValueError):
        target.decode_classes(classes, ("a",))


def test_exact_alphabet_cannot_drop_or_reorder_classes():
    tokens = [chr(0x4E00 + i) for i in range(target.CLASSES - 1)]
    alphabet = "".join(tokens)
    assert target.validate_tokens(tokens, alphabet) == tuple(tokens)
    with pytest.raises(ValueError):
        target.validate_tokens(tokens[:-1], alphabet)
    with pytest.raises(ValueError):
        target.validate_tokens(list(reversed(tokens)), alphabet)


def test_trunk_excludes_both_trainable_constants(model, monkeypatch, tmp_path):
    monkeypatch.setattr(target, "load_reviewed", lambda _: clone(model))
    output = tmp_path / "trunk.onnx"
    report = target.write_trunk(tmp_path / "unused", output)
    trunk = onnx.load(output)
    assert trunk.graph.output[0].name == target.FEATURE
    assert not set(target.SHAPES) & target._producers(trunk).keys()
    assert report["feature_channels"] == 120
    onnx.checker.check_model(trunk, full_check=True)
    with pytest.raises(FileExistsError):
        target.write_trunk(tmp_path / "unused", output)


def test_no_change_patch_preserves_exact_source_bytes(model, monkeypatch, tmp_path):
    source, output = tmp_path / "source.onnx", tmp_path / "copy.onnx"
    source.write_bytes(model.SerializeToString())
    monkeypatch.setattr(target, "load_reviewed", lambda _: clone(model))
    result = target.patch_head(source, output, target.RecognitionHead(model))
    assert output.read_bytes() == source.read_bytes()
    assert result["changed_constants"] == []


def test_patch_only_changes_declared_head_nodes(model, monkeypatch, tmp_path):
    source, output = tmp_path / "source.onnx", tmp_path / "patched.onnx"
    source.write_bytes(model.SerializeToString())
    monkeypatch.setattr(target, "load_reviewed", lambda _: clone(model))
    head = target.RecognitionHead(model)
    with torch.no_grad():
        head.weight[119, 18384] = 3
        head.bias[0] = 2
    report = target.patch_head(source, output, head)
    after = onnx.load(output)
    changed = [old.output[0] for old, new in zip(model.graph.node, after.graph.node, strict=True)
               if old.SerializeToString() != new.SerializeToString()]
    assert changed == [target.WEIGHT, target.BIAS] == report["changed_constants"]
    assert report["all_other_graph_content_unchanged"]
    assert target._validate(after)[target.WEIGHT][119, 18384] == 3


def test_patch_rejects_nonfinite_head_before_writing(model, monkeypatch, tmp_path):
    monkeypatch.setattr(target, "load_reviewed", lambda _: clone(model))
    head = target.RecognitionHead(model)
    with torch.no_grad():
        head.bias[0] = float("nan")
    destination = tmp_path / "invalid.onnx"
    with pytest.raises(ValueError, match="Invalid replacement"):
        target.patch_head(tmp_path / "unused", destination, head)
    assert not destination.exists()
