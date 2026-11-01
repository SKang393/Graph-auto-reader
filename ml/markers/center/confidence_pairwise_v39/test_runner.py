# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
from __future__ import annotations

from pathlib import Path
from types import SimpleNamespace
from uuid import uuid4

import pytest
import torch

from . import runner


REPO = Path(r"E:\Graph-auto-reader")


def _config() -> dict:
    return runner._read_json(REPO / runner.CONFIG_PATH)


def test_fixed_config_and_transitive_source_closure_match() -> None:
    config = _config()
    runner.validate_config(REPO, config)
    assert config["optimizer_steps_expected"] == 69160
    assert config["private_reads"] == config["sealed_runs_authorized"] == 0
    assert config["production_approval"] is False
    assert config["runner_source_paths"] == [path.as_posix() for path in runner.RUNNER_SOURCES]


def test_report_order_maps_each_row_to_exactly_one_authenticated_group() -> None:
    report = {"groups": [{"scope": scope, "rows": rows} for scope, rows in runner.TRAINING_ROWS.items()]}
    ids = runner.make_group_ids(report)

    assert ids.dtype == torch.int64
    assert ids.numel() == sum(runner.TRAINING_ROWS.values())
    assert ids[0].item() == 0
    assert ids[-1].item() == len(runner.TRAINING_ROWS) - 1
    for index, (scope, count) in enumerate(runner.TRAINING_ROWS.items()):
        start = sum(list(runner.TRAINING_ROWS.values())[:index])
        assert torch.all(ids[start:start + count] == index)


def test_report_group_reordering_or_count_change_fails_closed() -> None:
    rows = [{"scope": scope, "rows": count} for scope, count in runner.TRAINING_ROWS.items()]
    rows[0], rows[1] = rows[1], rows[0]
    with pytest.raises(ValueError, match="group order"):
        runner.make_group_ids({"groups": rows})
    rows = [{"scope": scope, "rows": count} for scope, count in runner.TRAINING_ROWS.items()]
    rows[0]["rows"] -= 1
    with pytest.raises(ValueError, match="row count"):
        runner.make_group_ids({"groups": rows})


def test_unsupported_interpreter_fails_before_gpu_discovery(monkeypatch: pytest.MonkeyPatch) -> None:
    monkeypatch.setattr(runner.sys, "executable", r"C:\not-the-project-venv\python.exe")
    monkeypatch.setattr(runner.torch.cuda, "is_available", lambda: pytest.fail("GPU query should follow interpreter pin"))
    with pytest.raises(RuntimeError, match="project GPU venv"):
        runner.require_rx6600xt(REPO)


def test_memory_preflight_refuses_before_canonical_admission(monkeypatch: pytest.MonkeyPatch) -> None:
    called = False

    def acquire(*args, **kwargs):
        nonlocal called
        called = True
        pytest.fail("low-memory refusal must precede canonical admission")

    monkeypatch.setattr(runner, "require_memory_headroom",
                        lambda: (_ for _ in ()).throw(RuntimeError("low memory")))
    monkeypatch.setattr(runner, "acquire_training_candidate", acquire)
    with pytest.raises(RuntimeError, match="low memory"):
        runner.acquire_after_pre_admission_checks(REPO)
    assert not called


def test_device_preflight_refuses_before_canonical_admission(monkeypatch: pytest.MonkeyPatch) -> None:
    called = False

    def acquire(*args, **kwargs):
        nonlocal called
        called = True
        pytest.fail("unsupported device refusal must precede canonical admission")

    monkeypatch.setattr(runner, "require_memory_headroom", lambda: {"available_physical_bytes": 10**10})
    monkeypatch.setattr(runner, "require_rx6600xt",
                        lambda _repo: (_ for _ in ()).throw(RuntimeError("unsupported device")))
    monkeypatch.setattr(runner, "acquire_training_candidate", acquire)
    with pytest.raises(RuntimeError, match="unsupported device"):
        runner.acquire_after_pre_admission_checks(REPO)
    assert not called


def test_memory_preflight_requires_conservative_available_headroom() -> None:
    result = runner.require_memory_headroom(runner.MIN_AVAILABLE_MEMORY_BYTES)
    assert result == {
        "available_physical_bytes": runner.MIN_AVAILABLE_MEMORY_BYTES,
        "minimum_required_bytes": runner.MIN_AVAILABLE_MEMORY_BYTES,
    }
    with pytest.raises(RuntimeError, match="No canonical training admission was attempted"):
        runner.require_memory_headroom(runner.MIN_AVAILABLE_MEMORY_BYTES - 1)


def test_cpu_optimizer_parameter_is_rejected_without_constructing_optimizer() -> None:
    cpu_parameter = SimpleNamespace(device=torch.device("cpu"), grad=None)
    fake_optimizer = SimpleNamespace(param_groups=[{"params": [cpu_parameter], "capturable": True}], state={})
    with pytest.raises(RuntimeError, match="outside the selected GPU"):
        runner.assert_optimizer_on_device(fake_optimizer, torch.device("cuda:0"))


def test_noncapturable_optimizer_is_rejected_before_a_step() -> None:
    fake_optimizer = SimpleNamespace(param_groups=[{"params": [], "capturable": False}], state={})
    with pytest.raises(RuntimeError, match="step state on the selected GPU"):
        runner.assert_optimizer_on_device(fake_optimizer, torch.device("cuda:0"))


def test_adamw_factory_pins_v37_defaults_and_gpu_capturable_without_constructing_it(
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    captured = {}
    sentinel = object()

    def fake_adamw(parameters, **kwargs):
        captured["parameters"] = tuple(parameters)
        captured["kwargs"] = kwargs
        return sentinel

    parameter = object()
    head = SimpleNamespace(parameters=lambda: iter((parameter,)))
    monkeypatch.setattr(runner.torch.optim, "AdamW", fake_adamw)

    assert runner.create_optimizer(head) is sentinel
    assert captured == {
        "parameters": (parameter,),
        "kwargs": {
            "lr": 0.0003,
            "betas": (0.9, 0.999),
            "eps": 1e-8,
            "weight_decay": 0.0001,
            "amsgrad": False,
            "maximize": False,
            "foreach": None,
            "capturable": True,
            "differentiable": False,
            "fused": None,
        },
    }


def test_missing_canonical_admission_stops_before_train_data_or_output(
    monkeypatch: pytest.MonkeyPatch
) -> None:
    repo = REPO
    allowed = repo / "artifacts" / "goal22-runs"
    output = allowed / f"pairwise-unregistered-admission-{uuid4().hex}"
    calls: list[str] = []

    def reject_admission(*args, **kwargs):
        calls.append("canonical-admission")
        raise RuntimeError("Training candidate is not authorized by the canonical ledger")

    def forbidden_load(*args, **kwargs):
        calls.append("train-data-load")
        raise AssertionError("Train data must remain unread before admission")

    monkeypatch.setattr(runner, "acquire_training_candidate", reject_admission)
    monkeypatch.setattr(runner, "load_training", forbidden_load)
    monkeypatch.setattr(runner, "require_memory_headroom",
                        lambda: {"available_physical_bytes": runner.MIN_AVAILABLE_MEMORY_BYTES,
                                 "minimum_required_bytes": runner.MIN_AVAILABLE_MEMORY_BYTES})
    monkeypatch.setattr(runner, "require_rx6600xt", lambda _repo: torch.device("cuda:0"))
    with pytest.raises(RuntimeError, match="not authorized"):
        runner.run(repo, output)

    assert calls == ["canonical-admission"]
    assert not output.exists()
