# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Authorization and schema dispatch checks for the V24 training runner."""

from __future__ import annotations

import hashlib
import inspect
import json
from pathlib import Path
from types import SimpleNamespace

import pytest

from ml.markers.center.mask_preserving_v24 import runtime_binding, runtime_binding_v2
from ml.markers.center.mask_preserving_v24 import train_p1 as runner
from ml.markers.center.mask_preserving_v24.runtime_inputs import RuntimeInputError


def _write_binding(root: Path, schema: str) -> tuple[dict, Path]:
    relative = Path("artifacts/runtime-binding.json")
    path = root / relative
    path.parent.mkdir(parents=True)
    payload = json.dumps({"schema": schema}).encode("utf-8")
    path.write_bytes(payload)
    return {
        "seed": 20260903,
        "runtime_training_input_binding_path": relative.as_posix(),
        "runtime_training_input_binding_sha256": hashlib.sha256(payload).hexdigest(),
    }, path


def test_default_config_and_runner_sources_remain_explicit() -> None:
    parameter = inspect.signature(runner.run).parameters["config_path"]
    assert parameter.default == runner.CONFIG_PATH
    assert Path("ml/markers/center/mask_preserving_v24/runtime_binding.py") in runner.RUNNER_SOURCE_PATHS
    assert Path("ml/markers/center/mask_preserving_v24/runtime_binding_v2.py") in runner.RUNNER_SOURCE_PATHS
    assert Path("ml/markers/center/mask_preserving_v24/coverage_dev_protocol.json") in runner.RUNNER_SOURCE_PATHS


def test_v1_binding_is_authenticated_then_dispatched(tmp_path: Path, monkeypatch) -> None:
    config, binding_path = _write_binding(tmp_path, runtime_binding.BINDING_SCHEMA)
    expected = object()
    calls = []
    monkeypatch.setattr(runner, "REPO_ROOT", tmp_path)
    monkeypatch.setattr(
        runtime_binding,
        "load_runtime_family_binding",
        lambda path, sha256: calls.append((path, sha256)) or expected,
    )
    monkeypatch.setattr(
        runtime_binding_v2,
        "load_runtime_family_binding_v2",
        lambda *_: pytest.fail("v2 loader received a v1 binding"),
    )

    actual, schema = runner._load_bound_family_inputs(config)

    assert actual is expected
    assert schema == runtime_binding.BINDING_SCHEMA
    assert calls == [(binding_path.resolve(), config["runtime_training_input_binding_sha256"])]


def test_v2_binding_is_authenticated_then_dispatched(tmp_path: Path, monkeypatch) -> None:
    config, binding_path = _write_binding(tmp_path, runtime_binding_v2.BINDING_SCHEMA)
    expected = object()
    calls = []
    monkeypatch.setattr(runner, "REPO_ROOT", tmp_path)
    monkeypatch.setattr(
        runtime_binding,
        "load_runtime_family_binding",
        lambda *_: pytest.fail("v1 loader received a v2 binding"),
    )
    monkeypatch.setattr(
        runtime_binding_v2,
        "load_runtime_family_binding_v2",
        lambda path, sha256: calls.append((path, sha256)) or expected,
    )

    actual, schema = runner._load_bound_family_inputs(config)

    assert actual is expected
    assert schema == runtime_binding_v2.BINDING_SCHEMA
    assert calls == [(binding_path.resolve(), config["runtime_training_input_binding_sha256"])]


@pytest.mark.parametrize("failure", ["stale-sha", "foreign-schema"])
def test_invalid_binding_fails_before_authorization(
    failure: str, tmp_path: Path, monkeypatch
) -> None:
    schema = (
        runtime_binding.BINDING_SCHEMA
        if failure == "stale-sha"
        else "graphreader.marker-runtime-training-input-binding.foreign"
    )
    config, _ = _write_binding(tmp_path, schema)
    if failure == "stale-sha":
        config["runtime_training_input_binding_sha256"] = "0" * 64
    monkeypatch.setattr(runner, "REPO_ROOT", tmp_path)
    monkeypatch.setattr(
        runtime_binding,
        "load_runtime_family_binding",
        lambda *_: pytest.fail("loader ran before binding authentication"),
    )
    monkeypatch.setattr(
        runtime_binding_v2,
        "load_runtime_family_binding_v2",
        lambda *_: pytest.fail("loader ran before binding authentication"),
    )
    monkeypatch.setattr(
        runner,
        "_acquire_candidate",
        lambda *_: pytest.fail("authorization ran before binding authentication and schema dispatch"),
    )
    monkeypatch.setattr(
        runner.torch.optim,
        "AdamW",
        lambda *_args, **_kwargs: pytest.fail("optimizer ran before binding authentication"),
    )
    monkeypatch.setattr(runner, "_configure", lambda *_: None)

    expected_error = RuntimeInputError if failure == "stale-sha" else RuntimeError
    with pytest.raises(expected_error):
        runner._prepare_bound_family_inputs(
            config, Path("training/retry11.json"), "1" * 64
        )


def test_overridden_config_identity_reaches_training_ledger(tmp_path: Path, monkeypatch) -> None:
    captured = {}
    config_path = Path("ml/markers/center/mask_preserving_v24/training/retry11.json")
    config_sha256 = "1" * 64
    authorization = SimpleNamespace(
        binding={
            "candidate_config_path": config_path.as_posix(),
            "candidate_config_sha256": config_sha256,
        }
    )
    monkeypatch.setattr(runner, "REPO_ROOT", tmp_path)

    def acquire(repo_root, **kwargs):
        captured["repo_root"] = repo_root
        captured.update(kwargs)
        return authorization

    monkeypatch.setattr(runner, "acquire_training_candidate", acquire)

    assert runner._acquire_candidate(config_path, config_sha256) is authorization
    assert captured["repo_root"] == tmp_path
    assert captured["config_path"] == config_path
    assert captured["runner_source_paths"] == runner.RUNNER_SOURCE_PATHS


def test_changed_config_identity_is_voided_before_training(tmp_path: Path, monkeypatch) -> None:
    config_path = Path("ml/markers/center/mask_preserving_v24/training/retry11.json")
    authorization = SimpleNamespace(
        binding={
            "candidate_config_path": config_path.as_posix(),
            "candidate_config_sha256": "2" * 64,
        }
    )
    voided = []
    monkeypatch.setattr(runner, "REPO_ROOT", tmp_path)
    monkeypatch.setattr(runner, "acquire_training_candidate", lambda *_, **__: authorization)
    monkeypatch.setattr(runner, "void_candidate", lambda actual, error: voided.append((actual, error)))

    with pytest.raises(RuntimeError, match="different configuration identity"):
        runner._acquire_candidate(config_path, "1" * 64)

    assert len(voided) == 1
    assert voided[0][0] is authorization


def test_config_loader_returns_exact_relative_path_and_byte_hash(
    tmp_path: Path, monkeypatch
) -> None:
    relative = Path("ml/markers/center/mask_preserving_v24/training/retry11.json")
    path = tmp_path / relative
    path.parent.mkdir(parents=True)
    payload = b'{"candidate_id":"P1","retry_count":11}'
    path.write_bytes(payload)
    monkeypatch.setattr(runner, "REPO_ROOT", tmp_path)

    actual_path, actual_sha256, config = runner._load_config(relative)

    assert actual_path.as_posix() == relative.as_posix()
    assert actual_sha256 == hashlib.sha256(payload).hexdigest()
    assert config == {"candidate_id": "P1", "retry_count": 11}


def test_cli_forwards_explicit_config_path(monkeypatch, capsys) -> None:
    captured = {}
    config_path = Path("ml/markers/center/mask_preserving_v24/training/retry11.json")

    def run(output_dir, checkpoint, v21_onnx, actual_config_path=runner.CONFIG_PATH):
        captured.update(
            output_dir=output_dir,
            checkpoint=checkpoint,
            v21_onnx=v21_onnx,
            config_path=actual_config_path,
        )
        return {"status": "controlled-test"}

    monkeypatch.setattr(runner, "run", run)
    monkeypatch.setattr(
        "sys.argv",
        [
            "train_p1.py",
            "--output-dir", "candidate-output",
            "--checkpoint", "checkpoint.pt",
            "--onnx", "checkpoint.onnx",
            "--config", config_path.as_posix(),
        ],
    )

    runner.main()

    assert captured["config_path"] == config_path
    assert json.loads(capsys.readouterr().out) == {"status": "controlled-test"}
