# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
import json

import pytest

from ml.markers.classifier.structural_context_v5 import runner


def config():
    return {**runner.RECIPE, "task": runner.TASK, "revision": runner.REVISION, "candidate_id": "P1",
            "source_checkpoint_sha256": runner.INITIALIZER_SHA256, "sealed_runs_authorized": 0, "private_reads": 0}


def test_training_authorization_precedes_output_or_dataset_access(tmp_path, monkeypatch):
    path = tmp_path/runner.CONFIG_PATH
    path.parent.mkdir(parents=True)
    path.write_text(json.dumps(config()))
    def reject(*args, **kwargs):
        raise RuntimeError("not authorized")
    def unread(*args, **kwargs):
        pytest.fail("Dataset must not be read without authorization")
    monkeypatch.setattr(runner, "acquire_training_candidate", reject)
    monkeypatch.setattr(runner, "load_cache", unread)
    output = tmp_path/"run"
    with pytest.raises(RuntimeError, match="not authorized"):
        runner.run(tmp_path, output)
    assert not output.exists()


def test_cpu_guard_is_required_before_training_pixels(tmp_path, monkeypatch):
    monkeypatch.delenv("GOAL22_CPU_CEILING_PERCENT", raising=False)
    with pytest.raises(RuntimeError, match="CPU guard"):
        runner.preflight(tmp_path, config(), runner.WorkBudget(tmp_path/"CANCEL"))


@pytest.mark.parametrize("key,value", [("epochs", 13), ("learning_rate", .001),
    ("source_checkpoint_sha256", "a"*64), ("private_reads", 1), ("sealed_runs_authorized", 1)])
def test_registered_recipe_and_read_scope_cannot_drift(key, value):
    value_config = config()
    value_config[key] = value
    with pytest.raises(ValueError):
        runner.validate_config(value_config)


def test_source_binding_includes_generator_preprocessing_loss_and_policy():
    paths = {p.as_posix() for p in runner.RUNNER_SOURCES}
    assert {"ml/markers/classifier/structural_context_cache.py",
            "ml/markers/classifier/graph_context_cache.py", "ml/markers/classifier/runtime_patches.py",
            "ml/markers/classifier/native_context_v3/runner.py", "ml/policy/evidence-policy.json",
            "tools/Run-TrainingCpuBudget.ps1"} <= paths
    assert len(paths) == len(runner.RUNNER_SOURCES)
