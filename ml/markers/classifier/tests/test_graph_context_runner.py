# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
import json

import pytest

from ml.markers.classifier.graph_context_v4 import runner


def test_training_authorization_precedes_output_or_dataset_access(tmp_path, monkeypatch):
    config = tmp_path / runner.CONFIG_PATH
    config.parent.mkdir(parents=True)
    config.write_text(json.dumps({}))

    def reject(*args, **kwargs):
        raise RuntimeError("not authorized")

    def unread(*args, **kwargs):
        pytest.fail("Dataset must not be read without authorization")

    monkeypatch.setattr(runner, "acquire_training_candidate", reject)
    monkeypatch.setattr(runner, "load_cache", unread)
    output = tmp_path / "run"
    with pytest.raises(RuntimeError, match="not authorized"):
        runner.run(tmp_path, output)
    assert not output.exists()


def test_cpu_guard_is_required_before_reading_training_pixels(tmp_path, monkeypatch):
    monkeypatch.delenv("GOAL22_CPU_CEILING_PERCENT", raising=False)
    config = {"task": runner.TASK, "revision": runner.REVISION, "candidate_id": "P1"}
    with pytest.raises(RuntimeError, match="CPU guard"):
        runner.preflight(tmp_path, config, runner.WorkBudget(tmp_path / "CANCEL"))


def test_source_binding_includes_generator_runtime_crop_policy_and_shared_loss():
    paths = {p.as_posix() for p in runner.RUNNER_SOURCES}
    assert {"ml/markers/classifier/graph_context_data.py", "ml/markers/classifier/graph_context_cache.py",
            "ml/markers/classifier/runtime_patches.py", "ml/markers/classifier/native_context_v3/runner.py",
            "ml/policy/evidence-policy.json", "tools/Run-TrainingCpuBudget.ps1"} <= paths
    assert len(paths) == len(runner.RUNNER_SOURCES)
