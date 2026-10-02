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
from ml.markers.center.confidence_head_v32 import cache, runner
from ml.markers.center.confidence_head_v32.adaptation import ConfidenceHead
from ml.markers.center.scale_classifier_v16.model import ScaleClassifierNet


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
        group = {"scope": scope, "rows": count, "positive": 1}
        tensor = tmp_path / f"{scope}-tensors.bin"
        tensor.write_bytes(scope.encode())
        group["tensors"] = reference(tensor)
        arrays = {"features": np.arange(count * 64, dtype=np.float32).reshape(count, 64),
                  "raw": np.zeros((count, 4), dtype=np.float32),
                  "labels": np.array([1] + [0] * (count - 1), dtype=np.float32),
                  "hard_negative": np.array([0] + [1] * (count - 1),
                      dtype=np.bool_ if scope == "component" else np.float32)}
        for name, value in arrays.items():
            path = tmp_path / f"{scope}-{name}.npy"
            np.save(path, value, allow_pickle=False)
            group[name] = reference(path)
        groups.append(group)
    report = {"status": "frozen_training_features_measured", "training_rows": 5,
              "model_parameters_and_buffers_unchanged": True, "extraction_check_max_absolute_error": 0,
              "optimizer_steps": 0, "private_reads": 0, "sealed_reads": 0, "development_tensor_reads": 0,
              "model": reference(model), "evidence": [reference(evidence)], "groups": groups}
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


@pytest.mark.parametrize("defect", ["drop_group", "reorder_groups", "change_count", "optimizer_steps",
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


def test_resumed_head_training_matches_uninterrupted_parameters_and_losses(tmp_path):
    torch.manual_seed(20261002)
    model = ScaleClassifierNet()
    training = (torch.rand(7, 64), torch.tensor([1., 0., 1., 0., 1., 0., 0.]),
                torch.tensor([0., 0., 0., 1., 0., 1., 0.]))
    recipe = runner.RECIPE | {"epochs": 2, "batch_size": 3}
    binding = {"fixed": "same initialization, recipe and training population"}
    paths = [tmp_path / name for name in ("full", "part", "resumed")]
    for path in paths:
        path.mkdir()
    heads = [ConfidenceHead(model) for _ in paths]
    optimizers = [torch.optim.AdamW(head.parameters(), lr=recipe["learning_rate"],
                                   weight_decay=recipe["weight_decay"]) for head in heads]
    expected = runner.train_epochs(heads[0], optimizers[0], training, recipe, binding, paths[0], BUDGET)
    runner.train_epochs(heads[1], optimizers[1], training, recipe | {"epochs": 1}, binding, paths[1], BUDGET)
    actual = runner.train_epochs(heads[2], optimizers[2], training, recipe, binding, paths[2], BUDGET,
                                 resume=paths[1] / "recovery.pt")
    assert actual["resumed_from_epoch"] == 1
    assert actual["optimizer_steps"] == expected["optimizer_steps"] == 6
    assert actual["history"] == expected["history"]
    for key, value in heads[0].state_dict().items():
        assert torch.equal(value, heads[2].state_dict()[key])
    with pytest.raises(ValueError, match="different frozen training"):
        runner.train_epochs(heads[2], optimizers[2], training, recipe, {"different": True}, paths[2], BUDGET,
                            resume=paths[1] / "recovery.pt")
    recovery = torch.load(paths[1] / "recovery.pt", weights_only=True)
    recovery["optimizer_steps"] += 1
    torch.save(recovery, paths[1] / "bad.pt")
    with pytest.raises(ValueError, match="step count changed"):
        runner.train_epochs(heads[2], optimizers[2], training, recipe, binding, paths[2], BUDGET,
                            resume=paths[1] / "bad.pt")


@pytest.mark.parametrize("key,value", [("private_reads", 1), ("sealed_runs_authorized", 1),
    ("source_checkpoint_sha256", "different"), ("production_approval", True), ("candidate_id", "P2"),
    ("recipe", runner.RECIPE | {"confidence_threshold": .25}),
    ("training_group_rows", runner.TRAINING_ROWS | {"crowded": 1})])
def test_config_rejects_widened_recipe_or_authorization(key, value):
    config = {"task": runner.TASK, "revision": runner.REVISION, "candidate_id": "P1", "recipe": runner.RECIPE,
              "training_group_rows": runner.TRAINING_ROWS, "source_checkpoint_sha256": runner.MODEL_SHA256,
              "private_reads": 0, "sealed_runs_authorized": 0, "production_approval": False}
    runner.validate_config(config)
    altered = copy.deepcopy(config)
    altered[key] = value
    with pytest.raises(ValueError, match="changed"):
        runner.validate_config(altered)


def test_numerical_parity_retains_boundary_sensitivity_as_evidence():
    runner.validate_export_parity({"maximum_absolute_error": 6.198883056640625e-6, "confidence_decision_changes": 1})


@pytest.mark.parametrize("error", [-.1, float('nan'), float('inf'), 1.00001e-5])
def test_numerical_parity_rejects_invalid_or_excessive_error(error):
    with pytest.raises(RuntimeError, match="numerical parity"):
        runner.validate_export_parity({"maximum_absolute_error": error, "confidence_decision_changes": 0})


def test_completed_recovery_cannot_execute_more_optimizer_steps(tmp_path):
    torch.manual_seed(71)
    head = ConfidenceHead(ScaleClassifierNet())
    optimizer = torch.optim.AdamW(head.parameters(), lr=.0003)
    training = (torch.rand(3, 64), torch.tensor([1., 0., 0.]), torch.zeros(3))
    recipe = runner.RECIPE | {"epochs": 1, "batch_size": 3}
    binding = {"test": "completed epoch"}
    runner.train_epochs(head, optimizer, training, recipe, binding, tmp_path, BUDGET)
    snapshot = {k: v.clone() for k, v in head.state_dict().items()}
    class NoSteps:
        def load_state_dict(self, state):
            pass
        def step(self):
            pytest.fail("Completed training must never step the optimizer")
    result = runner.train_epochs(head, NoSteps(), training, recipe, binding, tmp_path, BUDGET,
                                  resume=tmp_path / "recovery.pt", require_completed=True)
    assert result["resumed_from_epoch"] == result["completed_epochs"] == 1
    assert all(torch.equal(v, head.state_dict()[k]) for k, v in snapshot.items())
    with pytest.raises(ValueError, match="further optimization is forbidden"):
        runner.train_epochs(head, NoSteps(), training, recipe | {"epochs": 2}, binding, tmp_path, BUDGET,
                            resume=tmp_path / "recovery.pt", require_completed=True)


@pytest.mark.parametrize("defect", ["missing", "path", "checksum", "epochs", "steps"])
def test_recovery_requires_the_registered_complete_checkpoint(tmp_path, defect):
    path = tmp_path / "completed.pt"
    config = {"task": runner.TASK, "revision": runner.REVISION, "candidate_id": "P1", "recipe": runner.RECIPE,
              "feature_report_sha256": "feature-sha", "source_checkpoint_sha256": runner.MODEL_SHA256,
              "expected_runner_source_bundle_sha256": "new-source", "training_group_rows": runner.TRAINING_ROWS,
              "optimizer_steps_expected": 17296, "completed_training_recovery": {"path": str(path), "sha256": "checkpoint-sha",
                  "completed_epochs": 8, "optimizer_steps": 17296, "original_runner_source_bundle_sha256": "old-source"}}
    bound = runner.recovery_binding(config, path, "checkpoint-sha")
    assert bound["expected_runner_source_bundle_sha256"] == "old-source"
    assert bound["recipe"] == runner.RECIPE and bound["training_group_rows"] == runner.TRAINING_ROWS
    resume, checksum = path, "checkpoint-sha"
    if defect == "missing":
        resume = None
    elif defect == "path":
        resume = tmp_path / "other.pt"
    elif defect == "checksum":
        checksum = "different"
    elif defect == "epochs":
        config["completed_training_recovery"]["completed_epochs"] = 7
    else:
        config["completed_training_recovery"]["optimizer_steps"] = 0
    with pytest.raises(ValueError, match="registered completed training"):
        runner.recovery_binding(config, resume, checksum)
