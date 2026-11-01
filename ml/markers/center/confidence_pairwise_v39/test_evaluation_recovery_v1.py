# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
from __future__ import annotations

import ast
import inspect
from pathlib import Path
from uuid import uuid4

import pytest

from . import evaluation, evaluation_recovery_v1 as recovery


REPO = Path(r"E:\Graph-auto-reader")
FIXED_ARTIFACTS = (
    REPO / "artifacts/goal22-worktrees/resume-20260908/artifacts/goal22-runs/reflected-spatial-features-20261002-v1/report.json",
    REPO / "artifacts/goal22-worktrees/resume-20260908/artifacts/goal22-runs/center-shape-coverage-cache-v3/dev.pt",
)
ARTIFACTS_AVAILABLE = all(path.is_file() for path in FIXED_ARTIFACTS)


def test_preflight_merge_restores_all_runtime_keys_without_changing_checks(monkeypatch: pytest.MonkeyPatch) -> None:
    summary = {"status": "eval_preflight_pass_no_candidate_scored", "scene_total": 176}
    validated = {
        "data_root": Path("E:/validated-data"), "hashes": {"dev_file": "a" * 64},
        "scene_counts": {"component": (167, 2004)}, "scene_total": 176,
        "truth_total": 2210, "split_identity": {"component_dev_split": "b" * 64},
    }
    monkeypatch.setattr(evaluation, "preflight", lambda _root: summary)
    monkeypatch.setattr(evaluation, "validate_protocol", lambda *_args: validated)
    monkeypatch.setattr(evaluation, "_read_json", lambda _path: {})
    monkeypatch.setattr(evaluation, "_safe_project_file", lambda _root, relative: relative)

    result = recovery._full_preflight(Path("."))
    assert result["status"] == summary["status"]
    for key, value in validated.items():
        assert result[key] == value


@pytest.mark.skipif(not ARTIFACTS_AVAILABLE, reason="fixed local synthetic train/dev preflight artifacts are absent")
def test_actual_recovery_preflight_has_every_key_used_by_original_eval_run() -> None:
    result = recovery.preflight(REPO)
    required = {"data_root", "hashes", "scene_counts", "scene_total", "truth_total", "split_identity"}
    assert required.issubset(result)
    assert result["scene_total"] == 176
    assert result["truth_total"] == 2210
    assert result["hashes"]["dev_file"] == evaluation.DEV_SHA256
    assert result["optimizer_steps"] == result["private_reads"] == result["sealed_reads"] == 0


@pytest.mark.skipif(not (REPO / recovery.BINDING_PATH).is_file(), reason="reviewed recovery binding is absent")
def test_actual_recovery_binding_is_hash_pinned_and_uses_fresh_output() -> None:
    binding_path = REPO / recovery.BINDING_PATH
    binding = recovery._read_json(binding_path)
    value = recovery._validate_binding(
        REPO,
        evaluation.sha256_file(binding_path),
        binding["inputs"]["checkpoint_sha256"],
        binding["inputs"]["training_report_sha256"],
    )
    assert value["sha256"] == evaluation.sha256_file(binding_path)
    assert value["binding"]["output_path"] == recovery.OUTPUT_PATH.as_posix()
    assert value["binding"]["fixed_threshold"] == 0.1
    assert value["binding"]["matching_tolerance_px"] == 5.0


def test_recovery_run_passes_full_binding_and_fresh_output_to_original_eval(monkeypatch: pytest.MonkeyPatch,
                                                                            tmp_path: Path) -> None:
    full = {
        "data_root": REPO / "artifacts/goal22-worktrees/resume-20260908",
        "hashes": {"dev_file": evaluation.DEV_SHA256},
        "scene_counts": evaluation.DEV_COUNTS, "scene_total": 176, "truth_total": 2210,
        "split_identity": {"component_dev_split": "c" * 64},
    }
    output_relative = tmp_path.relative_to(REPO) / f"mock-eval-{uuid4().hex}"
    fake_pin = {"path": "mock-binding.json", "sha256": "d" * 64}
    monkeypatch.setattr(recovery, "_validate_binding", lambda *_args: fake_pin)
    monkeypatch.setattr(recovery, "_full_preflight", lambda _root: full)
    monkeypatch.setattr(recovery, "OUTPUT_PATH", output_relative)
    observed = {}

    def fake_eval_run(project_root, *, output, expected_checkpoint_sha256, expected_training_report_sha256):
        observed["preflight"] = evaluation.preflight(project_root)
        observed["output"] = output
        observed["checkpoint"] = expected_checkpoint_sha256
        observed["report"] = expected_training_report_sha256
        destination = project_root / output
        destination.mkdir(parents=True, exist_ok=False)
        (destination / "report.json").write_text('{"status":"exploratory_dev_scored_no_independent_acceptance"}\n', encoding="utf-8")
        return {"status": "exploratory_dev_scored_no_independent_acceptance", "private_reads": 0,
                "sealed_reads": 0, "optimizer_steps": 0}

    monkeypatch.setattr(evaluation, "run", fake_eval_run)
    receipt = recovery.run(REPO, expected_binding_sha256="d" * 64,
                           expected_checkpoint_sha256="a" * 64, expected_training_report_sha256="b" * 64)
    assert observed["preflight"] == full
    assert {"data_root", "hashes", "scene_counts", "scene_total", "truth_total", "split_identity"} <= set(observed["preflight"])
    assert observed["output"] == output_relative
    assert observed["checkpoint"] == "a" * 64
    assert observed["report"] == "b" * 64
    assert receipt["private_reads"] == receipt["sealed_reads"] == receipt["optimizer_steps"] == 0


def test_original_eval_binding_key_audit_is_complete() -> None:
    tree = ast.parse(inspect.getsource(evaluation.run))
    direct_binding_keys = {
        node.slice.value for node in ast.walk(tree)
        if isinstance(node, ast.Subscript) and isinstance(node.value, ast.Name) and
        node.value.id == "binding" and isinstance(node.slice, ast.Constant) and isinstance(node.slice.value, str)
    }
    assert direct_binding_keys == {"data_root", "hashes", "split_identity"}
