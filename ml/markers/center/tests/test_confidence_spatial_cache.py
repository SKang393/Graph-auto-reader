# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
from contextlib import nullcontext
import copy
import json
from types import SimpleNamespace

import numpy as np
import pytest
import torch

from ml.markers.gate_seal import sha256_file
from ml.markers.center.confidence_spatial_v35 import cache


BUDGET = SimpleNamespace(work_block=nullcontext)


@pytest.fixture
def feature_cache(tmp_path):
    def reference(path):
        return {"path": path.name, "sha256": sha256_file(path)}

    model = tmp_path / "model.bin"
    model.write_bytes(b"retained model")
    evidence = tmp_path / "evidence.json"
    evidence.write_text("{}")
    groups = []
    for scope, count in (("component", 3), ("family", 2)):
        group = {"scope": scope, "rows": count, "positive": 1, "negative": count - 1, "raw_reconstruction_max_absolute_error": 0}
        tensor = tmp_path / f"{scope}-tensors.bin"
        tensor.write_bytes(scope.encode())
        group["tensors"] = reference(tensor)
        arrays = {"features": np.arange(count * 768, dtype=np.float32).reshape(count, 768),
                  "raw": np.zeros((count, 4), dtype=np.float32),
                  "labels": np.array([1] + [0] * (count - 1), dtype=np.float32),
                  "hard_negative": np.array([0] + [1] * (count - 1),
                      dtype=np.bool_ if scope == "component" else np.float32)}
        for name, value in arrays.items():
            path = tmp_path / f"{scope}-{name}.npy"
            np.save(path, value, allow_pickle=False)
            group[name] = reference(path)
        groups.append(group)
    report = {"status": "frozen_training_spatial_features_measured", "training_rows": 5,
              "model_parameters_and_buffers_unchanged": True, "raw_reconstruction_max_absolute_error": 0, "feature_width": 768,
              "optimizer_steps": 0, "private_reads": 0, "sealed_reads": 0, "development_tensor_reads": 0,
              "model": reference(model), "parent_feature_report": reference(evidence), "evidence": [reference(evidence)], "groups": groups}
    path = tmp_path / "report.json"
    path.write_text(json.dumps(report))
    return tmp_path, path, report


def load_fixture(fixture):
    root, path, report = fixture
    return cache.load_training(root, path, sha256_file(path), {"component": 3, "family": 2},
                               report["model"]["sha256"], BUDGET)


def test_cache_retains_every_feature_label_and_hard_negative_in_source_order(feature_cache):
    root, _, report = feature_cache
    training, actual_report = load_fixture(feature_cache)
    assert actual_report == report
    for tensor, name in zip(training, ("features", "labels", "hard_negative"), strict=True):
        expected = np.concatenate([np.load(root / group[name]["path"]) for group in report["groups"]])
        assert tensor.dtype == torch.float32
        np.testing.assert_array_equal(tensor.numpy(), expected)


@pytest.mark.parametrize("payload", ["model", "evidence", "tensors", "features", "raw", "labels", "hard_negative"])
def test_cache_rejects_modified_input_before_training(feature_cache, payload):
    root, _, report = feature_cache
    item = report["model"] if payload == "model" else (
        report["evidence"][0] if payload == "evidence" else report["groups"][0][payload])
    with (root / item["path"]).open("ab") as stream:
        stream.write(b"changed")
    with pytest.raises(ValueError, match="changed"):
        load_fixture(feature_cache)


@pytest.mark.parametrize("defect", ["drop_group", "reorder_groups", "change_count", "optimizer_steps", "feature_width", "raw_reconstruction_max_absolute_error",
                                  "private_reads", "sealed_reads", "development_tensor_reads"])
def test_cache_rejects_changed_membership_or_read_scope(feature_cache, defect):
    _, path, report = feature_cache
    if defect == "drop_group":
        report["groups"].pop()
    elif defect == "reorder_groups":
        report["groups"].reverse()
    elif defect == "change_count":
        report["groups"][0]["rows"] -= 1
    else:
        report[defect] = 1
    path.write_text(json.dumps(report))
    with pytest.raises(ValueError, match="changed"):
        load_fixture(feature_cache)


@pytest.mark.parametrize("name,defect", [("features", "nan"), ("features", "columns"),
    ("features", "dtype"), ("labels", "nonbinary"), ("labels", "positive_count"),
    ("hard_negative", "nonbinary"), ("hard_negative", "dtype")])
def test_cache_rejects_malformed_tensors_even_when_their_hash_is_rebound(feature_cache, name, defect):
    root, path, report = feature_cache
    item = report["groups"][0][name]
    value = np.load(root / item["path"])
    if name == "hard_negative":
        value = value.astype(np.float32)
    if defect == "nan":
        value[0, 0] = np.nan
    elif defect == "columns":
        value = value[:, :-1]
    elif defect == "dtype":
        value = value.astype(np.float64)
    else:
        value[0] = .5 if defect == "nonbinary" else 0
    np.save(root / item["path"], value, allow_pickle=False)
    item["sha256"] = sha256_file(root / item["path"])
    path.write_text(json.dumps(report))
    with pytest.raises(ValueError, match="contract changed"):
        load_fixture(feature_cache)


